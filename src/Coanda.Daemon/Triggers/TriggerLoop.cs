using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;
using Coanda.Core.Types;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Triggers;

/// <summary>
/// The daemon's one trigger loop. It polls every project's record past its position, in global
/// sequence order, for parcel creations, parcel moves, flow run ends, and recorded external events. A
/// parcel entering a state its type binds a flow to starts that flow on the parcel; a flow run's end
/// starts, on its parcel, every published flow whose follow it matches and which accepts the
/// parcel's type, as the next link of the ended flow run's chain; an external event matching a
/// subscription in its project creates the subscription's parcel and starts its flow.
/// A refusal is logged with its rule and the loop goes on. A bound entry made while the parcel had
/// a live flow run, as when a flow run's own step moves its parcel into a bound state, is refused and
/// starts nothing, then or later; whether the parcel had one is read from the parcel as it stood at
/// the entry, never as it stands when the loop gets to it, so the outcome does not depend on timing.
/// A follow is not a state entry: when several flows follow the same end, a parcel holds one live
/// flow run at a time, so its followers start one after another, each on the release of the flow run
/// before it.
/// </summary>
/// <remarks>
/// The position is a document in the node tenant, written after each entry is handled, and fixed
/// before the daemon advertises its address: on a node where the loop has never run it is the end of
/// the existing record, so installing this version starts nothing on parcels already sitting in a
/// bound state. A stop between a start and the position being written replays that entry on the next
/// start, and the replay finds the work already done: a binding's start acts as a principal whose id
/// names the entry event, and is skipped when the parcel already has a flow run started by that
/// principal; a subscription's parcel records an origin naming the subscription and the event, and is
/// neither created again nor given a second flow run; a follow's start acts as a principal whose id
/// names the ended flow run, and is skipped when that flow run already has a follower of the flow. A
/// replayed entry that was refused is refused again, since the parcel's state at the entry does not
/// change. Deferred follows are one document per parcel in the parcel's project, and a follow
/// leaves it only once its start has been written or refused, so a replay finds the follower
/// already started and drops it.
/// </remarks>
public sealed class TriggerLoop(
    IDocumentStore store,
    ParcelService parcels,
    FlowRunService flowRuns,
    FlowService flows,
    TriggerService triggers,
    IFlowRunAdvancer advancer,
    TimeProvider clock,
    ILogger<TriggerLoop> logger) : IHostedService, IDisposable
{
    /// <summary>The principal the loop reads the record as.</summary>
    public static readonly Principal LoopPrincipal = new("coanda-triggers", Role.Agent);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private const int PageSize = 256;

    private readonly CancellationTokenSource stopping = new();
    private Task running = Task.CompletedTask;
    private long position;

    /// <summary>The principal a binding's start acts as: the Agent role, named for the binding and the entry event.</summary>
    public static Principal PrincipalFor(string typeName, FlowBinding binding, Guid entryEventId) =>
        new($"binding:{typeName}/{binding.State}/{binding.Flow}:entry:{entryEventId}", Role.Agent);

    /// <summary>The principal a follow's start acts as: the Agent role, named for the flow run whose end it follows.</summary>
    public static Principal PrincipalFor(Guid parentFlowRunId) => new($"follow:{parentFlowRunId}", Role.Agent);

    /// <summary>
    /// Reads the loop's position, or on a node where it has never run, writes the end of the existing
    /// record as its position. The daemon calls this before it writes its address file.
    /// </summary>
    public async Task FixPositionAsync(CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(Tenants.Node);
        TriggerPosition? stored = await session.LoadAsync<TriggerPosition>(TriggerPosition.LoopId, cancellationToken);
        if (stored is not null)
        {
            position = stored.Sequence;
            return;
        }

        position = await store.Database.FetchHighestEventSequenceNumber(cancellationToken);
        session.Store(new TriggerPosition { Sequence = position });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Starts polling from the fixed position on the daemon's own worker.</summary>
    public void Begin() => running = Task.Run(() => RunAsync(stopping.Token));

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        try
        {
            await running.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The daemon stopped before the trigger loop finished the entry it was handling.");
        }
    }

    public void Dispose() => stopping.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (await HandlePageAsync(cancellationToken))
                {
                }

                await Task.Delay(PollInterval, clock, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                // Reading the record failed; nothing past the position was handled, so the next poll reads it again.
                logger.LogError(failure, "The trigger loop could not read the record past sequence {Sequence}.", position);
                await Task.Delay(PollInterval, clock, CancellationToken.None);
            }
        }
    }

    /// <summary>Handles the next page of the record past the position; true when the position moved.</summary>
    private async Task<bool> HandlePageAsync(CancellationToken cancellationToken)
    {
        // The head is read before the projects are, so every event at or below it was committed
        // with its project's documents already in place: no project's first entry can slip past.
        long head = await store.Database.FetchHighestEventSequenceNumber(cancellationToken);
        if (head <= position)
        {
            return false;
        }

        long to = Math.Min(position + PageSize, head);
        foreach (IEvent entry in await ReadAsync(position, to, cancellationToken))
        {
            if (entry.Data is ParcelCreated or ParcelMoved or ParcelFlowRunReleased or FlowRunEnded or ExternalEventRecorded)
            {
                await HandleAsync(entry, cancellationToken);
                await SavePositionAsync(entry.Sequence, cancellationToken);
            }

            position = entry.Sequence;
        }

        // Sequences are contiguous, so whatever in the window no project holds (definitions, say) is passed too.
        position = to;
        await SavePositionAsync(position, cancellationToken);
        return true;
    }

    /// <summary>The events in every project with a sequence in (<paramref name="from"/>, <paramref name="to"/>], in global sequence order. A project is a tenant, and a read is a tenant's.</summary>
    private async Task<IReadOnlyList<IEvent>> ReadAsync(long from, long to, CancellationToken cancellationToken)
    {
        List<IEvent> page = [];
        foreach (string project in await ProjectsAsync(cancellationToken))
        {
            await using IQuerySession session = store.QuerySession(project);
            IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(e => e.Sequence > from && e.Sequence <= to, cancellationToken);
            page.AddRange(events);
        }

        return [.. page.OrderBy(e => e.Sequence)];
    }

    /// <summary>Every project with a parcel or an external event: the only projects whose record the loop reads.</summary>
    private async Task<IReadOnlyList<string>> ProjectsAsync(CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<string> withParcels = await session.Query<Parcel>().AnyTenant().Select(p => p.Project).Distinct().ToListAsync(cancellationToken);
        IReadOnlyList<string> withEvents = await session.Query<ExternalEvent>().AnyTenant().Select(e => e.Project).Distinct().ToListAsync(cancellationToken);
        return [.. withParcels.Union(withEvents, StringComparer.Ordinal)];
    }

    private async Task SavePositionAsync(long sequence, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(Tenants.Node);
        session.Store(new TriggerPosition { Sequence = sequence });
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Handles one entry. A refusal is logged with its rule; any other failure is logged too, and the
    /// entry is passed over rather than read again forever, so one bad entry never stops the loop.
    /// </summary>
    private async Task HandleAsync(IEvent entry, CancellationToken cancellationToken)
    {
        try
        {
            switch (entry.Data)
            {
                case ParcelCreated created:
                    // A parcel being created has had no flow run yet, so nothing was live at this entry.
                    await EnteredAsync(created.Project, created.ParcelId, created.TypeName, created.State, entry.Id, null, cancellationToken);
                    break;
                case ParcelMoved moved:
                    await MovedAsync(entry.TenantId, moved, entry, cancellationToken);
                    break;
                case ParcelFlowRunReleased released:
                    // A release starts the follows waiting on it and never a state entry.
                    await StartDeferredFollowsAsync(entry.TenantId, released.ParcelId, cancellationToken);
                    break;
                case FlowRunEnded ended:
                    await EndedAsync(entry.TenantId, ended, cancellationToken);
                    break;
                case ExternalEventRecorded external:
                    await ExternalAsync(external, cancellationToken);
                    break;
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DomainException refused)
        {
            logger.LogWarning("The trigger loop refused entry {Sequence}: {Rule}", entry.Sequence, refused.Message);
        }
        catch (Exception failure)
        {
            logger.LogError(failure, "The trigger loop failed on entry {Sequence} and passed over it.", entry.Sequence);
        }
    }

    /// <summary>
    /// A parcel moved: the entry is weighed against the parcel as it stood just before the move,
    /// folded from its stream up to the move's version, so a flow run live then refuses the entry
    /// even if it has ended by the time the loop reads the move.
    /// </summary>
    private async Task MovedAsync(string project, ParcelMoved moved, IEvent entry, CancellationToken cancellationToken)
    {
        Parcel before = await parcels.ShowAtAsync(LoopPrincipal, project, moved.ParcelId, entry.Version - 1, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(moved.ParcelId)} in project '{project}'.");

        await EnteredAsync(project, before.Id, before.TypeName, moved.ToState, entry.Id, before.LiveFlowRunId, cancellationToken);
    }

    /// <summary>
    /// A parcel entered a state: start the flow its type, as resolution returns it by name, binds
    /// there. An entry made while the parcel had a live flow run, <paramref name="liveAtEntry"/>, is
    /// refused with a rule naming that flow run; a start the flow run service refuses throws its rule.
    /// Either refusal reaches the loop's warning log, and nothing is kept to start it later.
    /// </summary>
    private async Task EnteredAsync(string project, Guid parcelId, string typeName, string state, Guid entryId, Guid? liveAtEntry, CancellationToken cancellationToken)
    {
        ParcelType? type = await parcels.ResolveTypeAsync(LoopPrincipal, project, typeName, cancellationToken);
        if (type is null)
        {
            return;
        }

        TriggerEvent entered = TriggerEvent.ParcelEntered(type.Name, state);
        FlowBinding? binding = type.FlowBindings.FirstOrDefault(b => Trigger.ForBinding(type, b).Matches(entered));
        if (binding is null)
        {
            return;
        }

        Principal principal = PrincipalFor(type.Name, binding, entryId);
        IReadOnlyList<FlowRun> existing = await flowRuns.ListAsync(LoopPrincipal, project, parcelId, cancellationToken);
        if (existing.Any(f => string.Equals(f.StartedBy, principal.Id, StringComparison.Ordinal)))
        {
            return;
        }

        if (liveAtEntry is Guid live)
        {
            throw new DomainException(
                $"A parcel has at most one live flow run; parcel {Ids.Short(parcelId)} had flow run {Ids.Short(live)} live when it entered '{state}', so binding {type.Name}/{binding.State} starts nothing for that entry.");
        }

        await StartAsync(principal, project, parcelId, binding.Flow, $"binding {type.Name}/{binding.State}", cancellationToken);
    }

    private async Task<bool> HasLiveFlowRunAsync(string project, Guid parcelId, CancellationToken cancellationToken)
    {
        Parcel? parcel = await parcels.ShowAsync(LoopPrincipal, project, parcelId, cancellationToken);
        return parcel?.LiveFlowRunId is not null;
    }

    /// <summary>
    /// A flow run ended: read as a <c>flow-run-ended</c> event with its flow, its outcome, and its
    /// parcel's type, it starts on that parcel every published flow of the project whose follow
    /// matches it and which accepts the type. The first starts now; the parcel then holds a live
    /// flow run, so each of the others waits for a release, in name order, except that a follower whose
    /// flow declares <c>closes</c> starts after every follower that does not: its end closes the
    /// parcel, and no follow starts on a closed parcel. Each refusal is logged and the other
    /// followers still start.
    /// </summary>
    private async Task EndedAsync(string project, FlowRunEnded ended, CancellationToken cancellationToken)
    {
        FlowRun parent = await flowRuns.ShowAsync(LoopPrincipal, project, ended.FlowRunId, cancellationToken)
            ?? throw new DomainException($"No flow run {Ids.Short(ended.FlowRunId)} in project '{project}'.");
        Parcel parcel = await parcels.ShowAsync(LoopPrincipal, project, parent.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(parent.ParcelId)} in project '{project}'.");
        TriggerEvent candidate = TriggerEvent.FlowRunEnded(parent.FlowName, ended.Outcome, parcel.TypeName);

        IReadOnlyList<Definition> published = await flows.ListAsync(LoopPrincipal, project, cancellationToken);
        IEnumerable<Flow> followers = published
            .Select(definition => Flow.FromJson(definition.Content))
            .Where(flow => flow.Follow is { } follow
                && Trigger.ForFollow(follow).Matches(candidate)
                && flow.Accepts.Any(type => DefinitionResolution.SameName(type, parcel.TypeName)))
            .OrderBy(flow => flow.Closes is not null);

        List<PendingFollow> waiting = [];
        foreach (Flow follower in followers)
        {
            try
            {
                if (await FollowAsync(project, parent, follower.Name, cancellationToken))
                {
                    waiting.Add(new PendingFollow(parent.Id, follower.Name, follower.Closes is not null));
                }
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                LogFollowRefused(follower.Name, parent.Id, refused);
            }
        }

        if (waiting.Count > 0)
        {
            await DeferFollowsAsync(project, parent.ParcelId, waiting, cancellationToken);
        }
    }

    /// <summary>
    /// Starts <paramref name="flowName"/> as a follower of <paramref name="parent"/>, unless that
    /// flow run already has a follower of the flow. True when the parcel holds a live flow run, so the
    /// follow has to wait for its release.
    /// </summary>
    private async Task<bool> FollowAsync(string project, FlowRun parent, string flowName, CancellationToken cancellationToken)
    {
        IReadOnlyList<FlowRun> existing = await flowRuns.ListAsync(LoopPrincipal, project, parent.ParcelId, cancellationToken);
        if (existing.Any(f => f.ParentFlowRunId == parent.Id && DefinitionResolution.SameName(f.FlowName, flowName)))
        {
            return false;
        }

        if (await HasLiveFlowRunAsync(project, parent.ParcelId, cancellationToken))
        {
            return true;
        }

        FlowRun flowRun;
        try
        {
            flowRun = await flowRuns.FollowAsync(PrincipalFor(parent.Id), project, parent.Id, flowName, cancellationToken);
        }
        catch (DomainException) when (!cancellationToken.IsCancellationRequested)
        {
            // A flow run started on the parcel between the check and the start: wait for its release too.
            if (!await HasLiveFlowRunAsync(project, parent.ParcelId, cancellationToken))
            {
                throw;
            }

            return true;
        }

        logger.LogInformation(
            "The end of flow run {Parent} started flow run {FlowRun} of '{Flow}' at depth {Depth} on parcel {Parcel} in project {Project}.",
            Ids.Short(parent.Id), Ids.Short(flowRun.Id), flowName, flowRun.Depth, Ids.Short(parent.ParcelId), project);
        _ = advancer.RequestAdvance(project, flowRun.Id);
        return false;
    }

    /// <summary>
    /// Adds follows to the ones waiting on the parcel, after those already waiting and without
    /// repeating one, then moves every follow that closes the parcel behind every follow that does
    /// not, keeping each group's order: a closing follow that started first would leave the others
    /// to be refused on a closed parcel.
    /// </summary>
    private async Task DeferFollowsAsync(string project, Guid parcelId, IReadOnlyList<PendingFollow> follows, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(project);
        DeferredFollows deferred = await session.LoadAsync<DeferredFollows>(parcelId, cancellationToken)
            ?? new DeferredFollows { Id = parcelId };
        deferred.Pending = [.. deferred.Pending.Union(follows).OrderBy(follow => follow.Closes)];
        session.Store(deferred);
        await session.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "{Count} follow(s) of parcel {Parcel} wait for the parcel's live flow run to be released before they start, one at a time.",
            follows.Count, Ids.Short(parcelId));
    }

    /// <summary>
    /// Starts the follows waiting on the parcel, oldest first, until one has to wait for a release
    /// again, which the first one started makes the next do. A follow leaves the list once it has
    /// started, was already started, or was refused.
    /// </summary>
    private async Task StartDeferredFollowsAsync(string project, Guid parcelId, CancellationToken cancellationToken)
    {
        DeferredFollows? deferred;
        await using (IQuerySession session = store.QuerySession(project))
        {
            deferred = await session.LoadAsync<DeferredFollows>(parcelId, cancellationToken);
        }

        if (deferred is null)
        {
            return;
        }

        List<PendingFollow> remaining = [.. deferred.Pending];
        while (remaining.Count > 0)
        {
            PendingFollow next = remaining[0];
            try
            {
                FlowRun parent = await flowRuns.ShowAsync(LoopPrincipal, project, next.ParentFlowRunId, cancellationToken)
                    ?? throw new DomainException($"No flow run {Ids.Short(next.ParentFlowRunId)} in project '{project}'.");
                if (await FollowAsync(project, parent, next.Flow, cancellationToken))
                {
                    break;
                }
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                LogFollowRefused(next.Flow, next.ParentFlowRunId, refused);
            }

            remaining.RemoveAt(0);
        }

        await using IDocumentSession write = store.LightweightSession(project);
        if (remaining.Count == 0)
        {
            write.Delete<DeferredFollows>(parcelId);
        }
        else
        {
            deferred.Pending = remaining;
            write.Store(deferred);
        }

        await write.SaveChangesAsync(cancellationToken);
    }

    private void LogFollowRefused(string flowName, Guid parentId, DomainException refused) =>
        logger.LogWarning(
            "Flow '{Flow}' follows the end of flow run {FlowRun}, but its start was refused: {Rule}",
            flowName, Ids.Short(parentId), refused.Message);

    /// <summary>An external event: every subscription in its project that matches it creates its parcel and starts its flow.</summary>
    private async Task ExternalAsync(ExternalEventRecorded external, CancellationToken cancellationToken)
    {
        TriggerEvent candidate = new(external.Kind, external.Fields);
        IReadOnlyList<Subscription> subscriptions = await triggers.ListSubscriptionsAsync(LoopPrincipal, external.Project, cancellationToken);
        foreach (Subscription subscription in subscriptions.Where(s => s.Trigger.Matches(candidate)))
        {
            try
            {
                await FireAsync(subscription, external, cancellationToken);
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Subscription {Subscription} on event {Event} was refused: {Rule}",
                    Ids.Short(subscription.Id), Ids.Short(external.EventId), refused.Message);
            }
        }
    }

    private async Task FireAsync(Subscription subscription, ExternalEventRecorded external, CancellationToken cancellationToken)
    {
        string project = external.Project;
        string origin = subscription.OriginFor(external.EventId);
        Parcel? parcel = await parcels.FindByOriginAsync(LoopPrincipal, project, origin, cancellationToken);
        if (parcel is null)
        {
            if (!external.Fields.TryGetValue(subscription.TitleField, out string? title))
            {
                throw new DomainException(
                    $"Subscription {Ids.Short(subscription.Id)} titles its parcel with the field '{subscription.TitleField}', which event {Ids.Short(external.EventId)} of kind '{external.Kind}' does not carry, so no parcel was created.");
            }

            parcel = await parcels.AddAsync(subscription.Principal, project, subscription.ParcelType, title, null, origin, cancellationToken);
        }

        IReadOnlyList<FlowRun> existing = await flowRuns.ListAsync(LoopPrincipal, project, parcel.Id, cancellationToken);
        if (existing.Count > 0)
        {
            return;
        }

        await StartAsync(subscription.Principal, project, parcel.Id, subscription.Flow, $"subscription {Ids.Short(subscription.Id)}", cancellationToken);
    }

    /// <summary>Starts the flow run through the flow run service and hands it to the runner, as the start endpoint does.</summary>
    private async Task StartAsync(Principal principal, string project, Guid parcelId, string flow, string cause, CancellationToken cancellationToken)
    {
        FlowRun flowRun = await flowRuns.StartAsync(principal, project, parcelId, flow, cancellationToken);
        logger.LogInformation(
            "The {Cause} started flow run {FlowRun} of '{Flow}' on parcel {Parcel} in project {Project}.",
            cause, Ids.Short(flowRun.Id), flow, Ids.Short(parcelId), project);
        _ = advancer.RequestAdvance(project, flowRun.Id);
    }
}
