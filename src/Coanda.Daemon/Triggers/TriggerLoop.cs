using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;
using Coanda.Core.Types;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Triggers;

/// <summary>
/// The daemon's one trigger loop. It polls every project's record past its position, in global
/// sequence order, for parcel creations, parcel moves, flow run ends, and recorded external events. A
/// parcel entering a stage its type binds a flow to starts that flow on the parcel; a flow run's end
/// starts, on its parcel, every published flow whose follow it matches and which accepts the
/// parcel's type, as the next link of the ended flow run's chain, and every published trigger on a flow
/// run's end that it matches creates a parcel derived from the ended flow run's parcel and flow run and starts
/// the trigger's flow there. An external event matching a published trigger whose target creates a parcel
/// creates that parcel and starts the target's flow on it; one matching a trigger whose target acts on the
/// event's parcel starts the target's flow on that parcel. Subscriptions, recorded before triggers were
/// definitions, stay in force: an external event matching one creates the subscription's parcel and starts
/// its flow, as the principal that recorded the subscription. A trigger fires when it is published and
/// resolves for the event's project, nearest scope first by name, and a module-scope trigger only in a project
/// its module is switched on for; it acts as a Member-role principal naming the trigger and the cause
/// (<see cref="TriggerKey"/>). A trigger that creates parcels creates none in a project once it has created as many
/// there in the hour before as its hourly cap (<see cref="TriggerCapCount"/>); an event over the cap is dropped with a
/// warning, and neither an act-on-parcel trigger nor a subscription is capped. A refusal is logged with its rule and the loop goes on. A bound entry made while the parcel had
/// a live flow run, as when a flow run's own step moves its parcel into a bound stage, is refused and
/// starts nothing, then or later; whether the parcel had one is read from the parcel as it stood at
/// the entry, never as it stands when the loop gets to it, so the outcome does not depend on timing.
/// A bound flow's start also waits until every parcel blocking the parcel at its entry is closed
/// (<see cref="WaitingEntries"/>), and only a node whose claims let it start the flow judges this: the
/// blockers are judged once the claims below let the start go ahead, a blocker missing from this node's
/// record counts as open, and when the last one closes the claims are weighed again before the flow starts.
/// A follow is not a stage entry: when several flows follow the same end, a parcel holds one live
/// flow run at a time, so its followers start one after another, each on the release of the flow run
/// before it.
/// Once events replicate, the loop reads every event, imported ones too, and starts a flow run only where
/// the parcel's claims let this node (<see cref="ClaimDecider.Triggers"/>), read as they stand when the loop
/// handles the start: on a parcel this node holds, whichever node recorded the event; on an unclaimed parcel,
/// only from an event this node recorded; on a parcel another node's claim wins, never. An entry on a parcel
/// whose own claim is not yet held waits (<see cref="HeldBackEntries"/>), and is handled again, as the claims
/// stand then, once the claim is held or lost. Act-on-parcel triggers and triggers on a flow run's end follow the
/// claims as a follow does: the first on the parcel the event names, the second on the ended flow run's parcel,
/// whether or not that parcel is closed or holds another live flow run, since the parcel it creates is a new one.
/// An external event another node recorded creates no parcel, neither a trigger's nor a subscription's, and starts an
/// act-on-parcel trigger's flow only on a parcel this node holds. An external event naming a parcel whose live flow run
/// its own wait-for-external-event step paused, waiting for the event's kind, before the event's entry, resumes that
/// flow run under the same claims, as the event's resumer (<see cref="ResumerFor"/>), and only on the node that started it.
/// </summary>
/// <remarks>
/// The position is a document in the node tenant, written after each entry is handled, and fixed
/// before the daemon advertises its address: on a node where the loop has never run it is the end of
/// the existing record, so installing this version starts nothing on parcels already sitting in a
/// bound stage. A stop between a start and the position being written replays that entry on the next
/// start, and the replay finds the work already done: a binding's start acts as a principal whose id
/// names the entry event by the id its origin gave it, and is skipped when the parcel already has a flow
/// run started by that principal, so a node that comes to hold a parcel does not start again an entry
/// another node's flow run already answered, even one stopped since; a subscription's parcel records an origin naming the subscription and the event, and is
/// neither created again nor given a second flow run; a trigger's parcel records an origin naming the trigger and
/// the external event or the ended flow run, and is not created again when the project holds a parcel of that origin
/// from any node, while one this node created with no flow run, as after a stop between the creation and the start,
/// is given its flow run then; an act-on-parcel trigger's start acts as a principal naming the trigger and the event,
/// and is skipped when the parcel already has a flow run that principal started, live or ended, on any node; a resume
/// for an external event writes nothing once the flow run's latest pause is no longer its waiting step's own from before
/// the event, or a resume already names the event; a
/// follow's start acts as a principal whose id
/// names the ended flow run, and is skipped when that flow run already has a follower of the flow. A
/// replayed entry that was refused is refused again, since the parcel's stage at the entry does not
/// change. Deferred follows are one document per parcel in the parcel's project, and a follow
/// leaves it only once its start has been written, refused, or has failed, so a replay finds the follower
/// already started and drops it. Once the loop has handled the record up to the head it read at start, every
/// queue whose parcel is free proceeds, so a queue no release is left to start, as one an earlier daemon left
/// behind a failed start, does not wait forever.
/// </remarks>
public sealed class TriggerLoop(
    IDocumentStore store,
    ParcelService parcels,
    FlowRunService flowRuns,
    FlowService flows,
    TriggerService triggers,
    IFlowRunAdvancer advancer,
    NodeIdentity identity,
    TimeProvider clock,
    ILogger<TriggerLoop> logger) : IHostedService, IDisposable
{
    /// <summary>The principal the loop reads the record as.</summary>
    public static readonly Principal LoopPrincipal = new("coanda-triggers", Role.Agent);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How often the loop looks again at the parcels whose entries it holds back.</summary>
    private static readonly TimeSpan HeldBackInterval = TimeSpan.FromSeconds(1);
    private const int PageSize = 256;

    private readonly CancellationTokenSource stopping = new();
    private readonly DefinitionResolver definitions = new(store);
    private Task running = Task.CompletedTask;
    private long position;

    // Whether any entry may be held back: true at start, for those a run before this one held back.
    private bool heldBack = true;
    private DateTimeOffset nextHeldBackLook = DateTimeOffset.MinValue;

    // The parcel whose held back entries are being handled again, which are not held back behind themselves.
    private Guid? replaying;

    /// <summary>
    /// The principal a binding's start acts as: the Agent role, named for the binding and the entry event, by the id
    /// the entry's origin gave it (<see cref="ImportedEvents.OriginalIdOf"/>), so every node names one entry alike.
    /// </summary>
    public static Principal PrincipalFor(string typeName, FlowBinding binding, Guid entryEventId) =>
        new($"binding:{typeName}/{binding.Stage}/{binding.Flow}:entry:{entryEventId}", Role.Agent);

    /// <summary>The principal a follow's start acts as: the Agent role, named for the flow run whose end it follows.</summary>
    public static Principal PrincipalFor(Guid parentFlowRunId) => new($"follow:{parentFlowRunId}", Role.Agent);

    /// <summary>
    /// Awaited after each follower of an end is started, set waiting, skipped, or refused, before the
    /// loop decides the next one. Null, as it is in a running daemon, awaits nothing; a test supplies it
    /// through <see cref="Api.DaemonOptions"/> to act between the choice for one follower and the next.
    /// </summary>
    internal Func<FollowChoice, CancellationToken, Task>? FollowDecided { get; set; }

    /// <summary>
    /// Awaited just before a follow from a parcel's follow queue starts on the free parcel. Null, as it is in a
    /// running daemon, awaits nothing; a test supplies it through <see cref="Api.DaemonOptions"/> to make that
    /// start fail, or to hold it open.
    /// </summary>
    internal Func<DeferredFollowStart, CancellationToken, Task>? DeferredFollowStarting { get; set; }

    /// <summary>
    /// Awaited just before a follower starts straight from a flow run's end on the free parcel. Null, as it is in
    /// a running daemon, awaits nothing; a test supplies it through <see cref="Api.DaemonOptions"/> to make that
    /// start fail, or to hold it open.
    /// </summary>
    internal Func<DirectFollowStart, CancellationToken, Task>? DirectFollowStarting { get; set; }

    /// <summary>
    /// Awaited just before a bound flow starts on a parcel because the last parcel blocking its entry closed. Null, as
    /// it is in a running daemon, awaits nothing; a test supplies it through <see cref="Api.DaemonOptions"/> to make
    /// that start fail.
    /// </summary>
    internal Func<WaitedEntryStart, CancellationToken, Task>? WaitedEntryStarting { get; set; }

    /// <summary>
    /// Awaited just before the follows of a flow run's end that wait are written to its parcel's follow queue, given
    /// that parcel's id. Null, as it is in a running daemon, awaits nothing; a test supplies it through
    /// <see cref="Api.DaemonOptions"/> to make that write fail.
    /// </summary>
    internal Func<Guid, CancellationToken, Task>? FollowsDeferring { get; set; }

    /// <summary>
    /// Awaited just before a subscription fires on an external event. Null, as it is in a running daemon, awaits
    /// nothing; a test supplies it through <see cref="Api.DaemonOptions"/> to make that firing fail.
    /// </summary>
    internal Func<Subscription, CancellationToken, Task>? SubscriptionFiring { get; set; }

    /// <summary>
    /// Reads the loop's position, or on a node where it has never run, writes the end of the existing
    /// record as its position. The daemon calls this before it writes its address file.
    /// </summary>
    public async Task SettlePositionAsync(CancellationToken cancellationToken)
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
        // The head of the record as the loop read it at start, which it handles up to before it resumes the
        // follow queues, so the resume never decides ahead of an entry already in the record.
        long? resumeAt = null;
        bool resumed = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!resumed)
                {
                    resumeAt ??= await store.Database.FetchHighestEventSequenceNumber(cancellationToken);
                    while (await HandlePageAsync(resumeAt.Value, cancellationToken))
                    {
                    }

                    resumed = true;
                    try
                    {
                        await ResumeFollowQueuesAsync(cancellationToken);
                    }
                    catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.LogError(failure, "The trigger loop could not resume the follow queues at start; each waits for its parcel's next release.");
                    }
                }

                while (await HandlePageAsync(long.MaxValue, cancellationToken))
                {
                }

                try
                {
                    await HandleHeldBackAsync(cancellationToken);
                }
                catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogError(failure, "The trigger loop could not handle again the entries it holds back; the next look tries again.");
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

    /// <summary>
    /// Handles the next page of the record past the position, going no further than <paramref name="limit"/>;
    /// true when the position moved.
    /// </summary>
    private async Task<bool> HandlePageAsync(long limit, CancellationToken cancellationToken)
    {
        // The head is read before the projects are, so every event at or below it was committed
        // with its project's documents already in place: no project's first entry can slip past.
        long head = Math.Min(await store.Database.FetchHighestEventSequenceNumber(cancellationToken), limit);
        if (head <= position)
        {
            return false;
        }

        long to = Math.Min(position + PageSize, head);
        foreach ((IEvent entry, bool own, Guid originalId) in await ReadAsync(position, to, cancellationToken))
        {
            if (entry.Data is ParcelCreated or ParcelMoved or ParcelClosed or ParcelFlowRunReleased or FlowRunEnded or ExternalEventRecorded)
            {
                await HandleAsync(entry, own, originalId, cancellationToken);
                await SavePositionAsync(entry.Sequence, cancellationToken);
            }

            position = entry.Sequence;
        }

        // Sequences are contiguous, so whatever in the window no project holds (definitions, say) is passed too.
        position = to;
        await SavePositionAsync(position, cancellationToken);
        return true;
    }

    /// <summary>
    /// The events in every project with a sequence in (<paramref name="from"/>, <paramref name="to"/>], in global
    /// sequence order, each with whether this node recorded it rather than the import appending it from another
    /// node's lines (<see cref="ImportedEvents"/>), and with the id its origin gave it
    /// (<see cref="ImportedEvents.OriginalIdOf"/>), which names the event alike on every node. A project is a
    /// tenant, and a read is a tenant's.
    /// </summary>
    private async Task<IReadOnlyList<(IEvent Entry, bool Own, Guid OriginalId)>> ReadAsync(long from, long to, CancellationToken cancellationToken)
    {
        List<(IEvent Entry, bool Own, Guid OriginalId)> page = [];
        foreach (string project in await ProjectsAsync(cancellationToken))
        {
            await using IQuerySession session = store.QuerySession(project);
            IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(e => e.Sequence > from && e.Sequence <= to, cancellationToken);
            page.AddRange(await TaggedAsync(session, events, cancellationToken));
        }

        return [.. page.OrderBy(e => e.Entry.Sequence)];
    }

    /// <summary>Each of <paramref name="events"/>, of <paramref name="session"/>'s project, with whether this node recorded it and the id its origin gave it.</summary>
    private async Task<IEnumerable<(IEvent Entry, bool Own, Guid OriginalId)>> TaggedAsync(
        IQuerySession session, IReadOnlyList<IEvent> events, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, LedgerLine> lines = await ImportedEvents.LinesAsync(session, [.. events.Select(e => e.Id)], cancellationToken);
        return events.Select(e => lines.TryGetValue(e.Id, out LedgerLine? line) && line.Origin != identity.NodeId
            ? (e, false, ImportedEvents.OriginalIdOf(e.Id, line))
            : (e, true, e.Id));
    }

    /// <summary>
    /// Handles again, in the order they were read, the entries held back on each parcel whose own claim is now held
    /// or lost, and keeps waiting those on a parcel whose claim is still not yet held. It looks at most once a
    /// <see cref="HeldBackInterval"/>, and not at all once a look found nothing held back, until an entry is again.
    /// A stop before the document is cleared handles the entries again on the next start, which finds the work done.
    /// </summary>
    private async Task HandleHeldBackAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        if (!heldBack || now < nextHeldBackLook)
        {
            return;
        }

        nextHeldBackLook = now + HeldBackInterval;
        bool waiting = false;
        foreach (string project in await ProjectsAsync(cancellationToken))
        {
            IReadOnlyList<HeldBackEntries> held;
            await using (IQuerySession session = store.QuerySession(project))
            {
                held = await session.Query<HeldBackEntries>().ToListAsync(cancellationToken);
            }

            foreach (HeldBackEntries parcel in held)
            {
                if (await StandingAsync(project, parcel.Id, cancellationToken) is ClaimStanding.NotYetHeld)
                {
                    waiting = true;
                    continue;
                }

                long[] sequences = [.. parcel.Sequences];
                List<(IEvent Entry, bool Own, Guid OriginalId)> entries;
                await using (IQuerySession session = store.QuerySession(project))
                {
                    IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(e => sequences.Contains(e.Sequence), cancellationToken);
                    entries = [.. (await TaggedAsync(session, events, cancellationToken)).OrderBy(e => e.Entry.Sequence)];
                }

                replaying = parcel.Id;
                try
                {
                    foreach ((IEvent entry, bool own, Guid originalId) in entries)
                    {
                        await HandleAsync(entry, own, originalId, cancellationToken);
                    }
                }
                finally
                {
                    replaying = null;
                }

                // An entry held back again while these were handled stays.
                await using IDocumentSession write = store.LightweightSession(project);
                HeldBackEntries? current = await write.LoadAsync<HeldBackEntries>(parcel.Id, cancellationToken);
                List<long> left = [.. current?.Sequences.Except(sequences) ?? []];
                if (left.Count == 0)
                {
                    write.Delete<HeldBackEntries>(parcel.Id);
                }
                else
                {
                    waiting = true;
                    write.Store(new HeldBackEntries { Id = parcel.Id, Sequences = left });
                }

                await write.SaveChangesAsync(cancellationToken);
            }
        }

        heldBack = waiting;
    }

    /// <summary>Keeps <paramref name="sequence"/>'s entry on the parcel to be handled again once this node's claim on it is held or lost.</summary>
    private async Task HoldBackAsync(string project, Guid parcelId, long sequence, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(project);
        HeldBackEntries held = await session.LoadAsync<HeldBackEntries>(parcelId, cancellationToken)
            ?? new HeldBackEntries { Id = parcelId };
        if (!held.Sequences.Contains(sequence))
        {
            held.Sequences.Add(sequence);
        }

        session.Store(held);
        await session.SaveChangesAsync(cancellationToken);
        heldBack = true;
    }

    private async Task<bool> HasHeldBackAsync(string project, Guid parcelId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(project);
        return await session.LoadAsync<HeldBackEntries>(parcelId, cancellationToken) is not null;
    }

    /// <summary>The parcel's standing among its claims now, or null when there is no such parcel.</summary>
    private async Task<ClaimStanding?> StandingAsync(string project, Guid parcelId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(project);
        Parcel? parcel = await session.LoadAsync<Parcel>(parcelId, cancellationToken);
        return parcel is null
            ? null
            : await ParcelClaims.StandingAsync(store, session, parcel, cancellationToken);
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
    /// <paramref name="originalId"/> is the id the entry's origin gave it, which a binding's start is keyed by.
    /// </summary>
    private async Task HandleAsync(IEvent entry, bool own, Guid originalId, CancellationToken cancellationToken)
    {
        try
        {
            switch (entry.Data)
            {
                case ParcelCreated created:
                    // A parcel being created has had no flow run yet, so nothing was live at this entry, and the parcel its
                    // creation folds to lists no relation, so nothing blocks it.
                    await EnteredAsync(
                        new StageEntry(created.Project, created.ParcelId, created.TypeName, created.Stage, originalId, entry.Version, null, [], own, entry.Sequence),
                        cancellationToken);
                    break;
                case ParcelMoved moved:
                    await MovedAsync(entry.TenantId, moved, entry, originalId, own, cancellationToken);
                    break;
                case ParcelClosed closed:
                    await ClosedAsync(entry.TenantId, closed.ParcelId, cancellationToken);
                    break;
                case ParcelFlowRunReleased released:
                    // A release starts the follows waiting on it and never a stage entry.
                    await StartDeferredFollowsAsync(entry.TenantId, released.ParcelId, entry.Sequence, cancellationToken);
                    break;
                case FlowRunEnded ended:
                    await EndedAsync(entry.TenantId, ended, own, entry.Sequence, cancellationToken);
                    break;
                case ExternalEventRecorded external:
                    await ExternalAsync(external, own, entry.Sequence, cancellationToken);
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
    /// even if it has ended by the time the loop reads the move, and only a blocked-by the parcel
    /// listed then holds the entry's start back. A move changes no relation, so the parcel lists the
    /// same relations just before the move as at it.
    /// </summary>
    private async Task MovedAsync(string project, ParcelMoved moved, IEvent entry, Guid entryId, bool own, CancellationToken cancellationToken)
    {
        Parcel before = await parcels.ShowAtAsync(LoopPrincipal, project, moved.ParcelId, entry.Version - 1, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(moved.ParcelId)} in project '{project}'.");
        Guid[] blockedBy = [.. before.Relations
            .Where(r => string.Equals(r.Kind, RelationKinds.BlockedBy, StringComparison.Ordinal))
            .Select(r => r.RelatedParcelId)
            .Distinct()];

        await EnteredAsync(
            new StageEntry(project, before.Id, before.TypeName, moved.ToStage, entryId, entry.Version, before.LiveFlowRunId, blockedBy, own, entry.Sequence),
            cancellationToken);
    }

    /// <summary>
    /// A parcel entered a stage: start the flow its type, as resolution returns it by name, binds
    /// there. An entry made while the parcel had a live flow run is refused with a rule naming that
    /// flow run; a start the flow run service refuses throws its rule. Either refusal reaches the
    /// loop's warning log, and nothing is kept to start it later. Once the claims let this node start
    /// the flow and the entry is not refused, the parcels that blocked the parcel at the entry are
    /// judged: while any of them is not closed in this node's record, one this node has not imported
    /// included, the entry waits (<see cref="WaitingEntries"/>) and is handled again when one of them
    /// closes (<see cref="ClosedAsync"/>). An entry handled again once its last blocker is closed
    /// starts the flow, as the same principal a start at the entry would have used, unless the parcel
    /// is closed, holds a live flow run, or has moved since the entry; it no longer waits either way.
    /// </summary>
    private async Task EnteredAsync(StageEntry entry, CancellationToken cancellationToken)
    {
        (string project, Guid parcelId, string stage) = (entry.Project, entry.ParcelId, entry.Stage);
        ParcelType? type = await parcels.ResolveTypeAsync(LoopPrincipal, project, entry.TypeName, cancellationToken);
        if (type is null)
        {
            return;
        }

        TriggerEvent entered = TriggerEvent.ParcelEntered(type.Name, stage);
        FlowBinding? binding = type.FlowBindings.FirstOrDefault(b => Trigger.ForBinding(type, b).Matches(entered));
        if (binding is null)
        {
            return;
        }

        switch (await GateAsync(project, parcelId, entry.Own, entry.Sequence, $"its entry into '{stage}'", cancellationToken))
        {
            case Gate.HeldBack:
                return;
            case Gate.Denied:
                // A wait whose end the claims no longer let this node judge starts nothing here, then or later.
                await StopWaitingAsync(project, parcelId, entry.Version, cancellationToken);
                return;
        }

        Principal principal = PrincipalFor(type.Name, binding, entry.EntryId);
        IReadOnlyList<FlowRun> existing = await flowRuns.ListAsync(LoopPrincipal, project, parcelId, cancellationToken);
        if (existing.Any(f => string.Equals(f.StartedBy, principal.Id, StringComparison.Ordinal)))
        {
            await StopWaitingAsync(project, parcelId, entry.Version, cancellationToken);
            return;
        }

        if (entry.LiveAtEntry is Guid live)
        {
            throw new DomainException(
                $"A parcel has at most one live flow run; parcel {Ids.Short(parcelId)} had flow run {Ids.Short(live)} live when it entered '{stage}', so binding {type.Name}/{binding.Stage} starts nothing for that entry.");
        }

        IReadOnlyList<Guid> open = await OpenBlockersAsync(project, entry.BlockedBy, cancellationToken);
        if (open.Count > 0)
        {
            await WaitAsync(project, parcelId, new WaitingEntry(entry.Version, entry.BlockedBy), cancellationToken);
            logger.LogInformation(
                "Parcel {Parcel} in project {Project} entered '{Stage}', which binds flow '{Flow}', while parcel(s) {Blockers} blocking it are not closed, so the flow starts only once every one of them is.",
                Ids.Short(parcelId), project, stage, binding.Flow, string.Join(", ", open.Select(Ids.Short)));
            return;
        }

        string cause = $"binding {type.Name}/{binding.Stage}";
        if (!await IsWaitingAsync(project, parcelId, entry.Version, cancellationToken))
        {
            await StartAsync(principal, project, parcelId, binding.Flow, cause, cancellationToken);
            return;
        }

        if (await WhyNothingStartsAsync(project, parcelId, entry.Version, cancellationToken) is { } why)
        {
            await StopWaitingAsync(project, parcelId, entry.Version, cancellationToken);
            logger.LogInformation(
                "Every parcel blocking parcel {Parcel} in project {Project} at its entry into '{Stage}' is closed, but flow '{Flow}' starts nothing for that entry, since {Why}; the entry no longer waits.",
                Ids.Short(parcelId), project, stage, binding.Flow, why);
            return;
        }

        try
        {
            if (WaitedEntryStarting is { } starting)
            {
                await starting(new WaitedEntryStart(project, parcelId, binding.Flow), cancellationToken);
            }

            await StartAsync(principal, project, parcelId, binding.Flow, cause, cancellationToken);
        }
        catch (DomainException) when (!cancellationToken.IsCancellationRequested)
        {
            // The refusal reaches the loop's warning log; the entry it refused no longer waits.
            await StopWaitingAsync(project, parcelId, entry.Version, cancellationToken);
            throw;
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            await StopWaitingAsync(project, parcelId, entry.Version, cancellationToken);
            logger.LogError(
                failure,
                "Starting flow '{Flow}' on parcel {Parcel} in project {Project}, whose blockers at its entry into '{Stage}' are all closed, failed; the entry no longer waits.",
                binding.Flow, Ids.Short(parcelId), project, stage);
            return;
        }

        await StopWaitingAsync(project, parcelId, entry.Version, cancellationToken);
    }

    /// <summary>
    /// A parcel closed: every entry waiting on it is handled again, each on its own, as the record holds that entry
    /// and with whether this node recorded it, so one entry's refusal or failure leaves the others to be handled. A
    /// stop leaves the close unhandled, and the next start handles it again.
    /// </summary>
    private async Task ClosedAsync(string project, Guid closedId, CancellationToken cancellationToken)
    {
        IReadOnlyList<WaitingEntries> waiting;
        await using (IQuerySession session = store.QuerySession(project))
        {
            waiting = await session.Query<WaitingEntries>().ToListAsync(cancellationToken);
        }

        foreach (WaitingEntries parcel in waiting)
        {
            foreach (WaitingEntry waits in parcel.Entries.Where(e => e.Blockers.Contains(closedId)).OrderBy(e => e.Version))
            {
                (IEvent Entry, bool Own, Guid OriginalId)? entry;
                await using (IQuerySession session = store.QuerySession(project))
                {
                    IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcel.Id, waits.Version, token: cancellationToken);
                    IEvent[] at = [.. events.Where(e => e.Version == waits.Version)];
                    entry = at.Length == 0 ? null : (await TaggedAsync(session, at, cancellationToken)).First();
                }

                if (entry is not { } found)
                {
                    await StopWaitingAsync(project, parcel.Id, waits.Version, cancellationToken);
                    logger.LogWarning(
                        "Parcel {Parcel} in project {Project} has no entry at version {Version} of its stream, so that entry no longer waits.",
                        Ids.Short(parcel.Id), project, waits.Version);
                    continue;
                }

                await HandleAsync(found.Entry, found.Own, found.OriginalId, cancellationToken);
            }
        }
    }

    /// <summary>The parcels of <paramref name="blockers"/> that are not closed in this node's record, a parcel it does not hold among them.</summary>
    private async Task<IReadOnlyList<Guid>> OpenBlockersAsync(string project, IReadOnlyList<Guid> blockers, CancellationToken cancellationToken)
    {
        if (blockers.Count == 0)
        {
            return [];
        }

        await using IQuerySession session = store.QuerySession(project);
        List<Guid> open = [];
        foreach (Guid blocker in blockers)
        {
            if (await session.LoadAsync<Parcel>(blocker, cancellationToken) is not { IsClosed: true })
            {
                open.Add(blocker);
            }
        }

        return open;
    }

    /// <summary>
    /// Why nothing starts for the parcel's entry at <paramref name="version"/> once its blockers are all closed, as the
    /// parcel stands now, or null when the flow starts: the parcel is closed, holds a live flow run, or has moved since.
    /// </summary>
    private async Task<string?> WhyNothingStartsAsync(string project, Guid parcelId, long version, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(project);
        Parcel? parcel = await session.LoadAsync<Parcel>(parcelId, cancellationToken);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: cancellationToken);
        return parcel switch
        {
            null => "the parcel is no longer in this node's record",
            { IsClosed: true } => $"the parcel is closed ({parcel.Resolution})",
            { LiveFlowRunId: Guid live } => $"the parcel holds flow run {Ids.Short(live)} live",
            _ when events.Any(e => e.Version > version && e.Data is ParcelMoved) => "the parcel has moved since that entry",
            _ => null,
        };
    }

    /// <summary>Keeps the parcel's entry waiting for its blockers, unless it already waits.</summary>
    private async Task WaitAsync(string project, Guid parcelId, WaitingEntry entry, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(project);
        WaitingEntries waiting = await session.LoadAsync<WaitingEntries>(parcelId, cancellationToken)
            ?? new WaitingEntries { Id = parcelId };
        if (waiting.Entries.Any(e => e.Version == entry.Version))
        {
            return;
        }

        waiting.Entries.Add(entry);
        session.Store(waiting);
        await session.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsWaitingAsync(string project, Guid parcelId, long version, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(project);
        WaitingEntries? waiting = await session.LoadAsync<WaitingEntries>(parcelId, cancellationToken);
        return waiting?.Entries.Any(e => e.Version == version) == true;
    }

    /// <summary>Takes the parcel's entry at <paramref name="version"/> out of the entries that wait, if it is among them.</summary>
    private async Task StopWaitingAsync(string project, Guid parcelId, long version, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(project);
        WaitingEntries? waiting = await session.LoadAsync<WaitingEntries>(parcelId, cancellationToken);
        if (waiting is null || waiting.Entries.RemoveAll(e => e.Version == version) == 0)
        {
            return;
        }

        if (waiting.Entries.Count == 0)
        {
            session.Delete<WaitingEntries>(parcelId);
        }
        else
        {
            session.Store(waiting);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Whether the parcel's claims, as they stand now, let this node start a flow run for an event it recorded when
    /// <paramref name="own"/> (<see cref="ClaimDecider.Triggers"/>); when they do not, says so in the log, naming
    /// <paramref name="cause"/>, the event the start would have answered. While this node's own claim on the
    /// parcel is not yet held, the entry at <paramref name="sequence"/> is held back to be handled again, and so is
    /// any entry on a parcel that already has entries held back, so they are handled in the order they were read.
    /// </summary>
    private async Task<bool> TriggersAsync(string project, Guid parcelId, bool own, long sequence, string cause, CancellationToken cancellationToken) =>
        await GateAsync(project, parcelId, own, sequence, cause, cancellationToken) is Gate.Starts;

    /// <summary>What the parcel's claims let this node do for an event: start, hold the entry back, or start nothing (<see cref="TriggersAsync"/>).</summary>
    private enum Gate
    {
        Starts,
        HeldBack,
        Denied,
    }

    /// <summary>As <see cref="TriggersAsync"/>, saying also whether an entry the claims do not let start yet was held back.</summary>
    private async Task<Gate> GateAsync(string project, Guid parcelId, bool own, long sequence, string cause, CancellationToken cancellationToken)
    {
        if (parcelId != replaying && await HasHeldBackAsync(project, parcelId, cancellationToken))
        {
            await HoldBackAsync(project, parcelId, sequence, cancellationToken);
            logger.LogInformation(
                "Parcel {Parcel} in project {Project} holds back {Cause} behind the entries it already holds back.",
                Ids.Short(parcelId), project, cause);
            return Gate.HeldBack;
        }

        ClaimStanding? standing = await StandingAsync(project, parcelId, cancellationToken);
        if (standing is null || ClaimDecider.Triggers(standing, own))
        {
            // With no such parcel, the start that follows says so.
            return Gate.Starts;
        }

        if (standing is ClaimStanding.NotYetHeld held)
        {
            await HoldBackAsync(project, parcelId, sequence, cancellationToken);
            logger.LogInformation(
                "Parcel {Parcel} in project {Project} holds back {Cause} until this node's claim on it is held or lost, since it is not yet held: {Why}.",
                Ids.Short(parcelId), project, cause, held.Reason);
            return Gate.HeldBack;
        }

        string why = standing switch
        {
            ClaimStanding.Unclaimed => "the parcel is unclaimed and another node recorded the event",
            ClaimStanding.Lost lost => $"node {lost.Winner}'s claim wins the parcel",
            _ => "this node does not hold the parcel",
        };
        logger.LogInformation(
            "Parcel {Parcel} in project {Project} starts nothing here for {Cause}, since {Why}.",
            Ids.Short(parcelId), project, cause, why);
        return Gate.Denied;
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
    /// parcel, and no follow starts on a closed parcel. Once one follower of the end waits, every
    /// later follower of it that has not already started waits behind it, even if the parcel is free
    /// again by the time the loop reaches it: a closing follower started ahead of a waiting one would
    /// close the parcel, and the waiting one's start would then be refused, and a later follower
    /// started ahead of an earlier one would break the name order. A closing follower also waits
    /// whenever follows of an earlier end are already waiting on the parcel, behind every one of them
    /// that does not close. A follower whose decision is refused, or fails for any other reason, is logged
    /// and passed over: it neither starts nor waits, and every later follower is decided as though it had
    /// not matched, so the follows already set to wait are still written to the parcel's follow queue. A
    /// stop is no failure of the follower: it leaves the end unhandled, and the next start handles it again,
    /// where a follower that already started is not started twice. Once the follows are decided, every trigger
    /// on a flow run's end that matches the end fires (<see cref="EndTriggersAsync"/>), under the same claims.
    /// </summary>
    private async Task EndedAsync(string project, FlowRunEnded ended, bool own, long sequence, CancellationToken cancellationToken)
    {
        FlowRun parent = await flowRuns.ShowAsync(LoopPrincipal, project, ended.FlowRunId, cancellationToken)
            ?? throw new DomainException($"No flow run {Ids.Short(ended.FlowRunId)} in project '{project}'.");
        Parcel parcel = await parcels.ShowAsync(LoopPrincipal, project, parent.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(parent.ParcelId)} in project '{project}'.");
        if (!await TriggersAsync(project, parcel.Id, own, sequence, $"the end of flow run {Ids.Short(parent.Id)}", cancellationToken))
        {
            return;
        }

        TriggerEvent candidate = TriggerEvent.FlowRunEnded(parent.FlowName, ended.Outcome, parcel.TypeName, parcel.Title);

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
            bool waits = false;
            try
            {
                // A closing follower also waits behind every follow already waiting on the parcel,
                // whichever end that follow came from, even if the parcel is free. Among the followers,
                // only a closing one's end closes the parcel, and a follow's start is refused on a closed
                // parcel, so a closing one started now would leave every waiting follow to be refused.
                // A follower that does not close is not held by follows of an earlier end.
                bool held = waiting.Count > 0
                    || (follower.Closes is not null && await HasDeferredFollowsAsync(project, parent.ParcelId, cancellationToken));
                waits = held
                    ? !await FollowedAsync(project, parent, follower.Name, cancellationToken)
                    : await FollowAsync(project, parent, follower.Name, fromQueue: false, cancellationToken);
                if (waits)
                {
                    waiting.Add(new PendingFollow(parent.Id, follower.Name, follower.Closes is not null));
                }
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                LogFollowRefused(follower.Name, parent.Id, refused);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(
                    failure,
                    "Flow '{Flow}' follows the end of flow run {FlowRun}, but deciding it failed, so it neither starts nor waits and the next follower goes on.",
                    follower.Name, Ids.Short(parent.Id));
            }

            if (FollowDecided is { } decided)
            {
                await decided(new FollowChoice(project, parent.Id, follower.Name, waits), cancellationToken);
            }
        }

        // The triggers on the end run even when writing the waiting follows fails, since what they create is a new
        // parcel no follow waits on; the failure still leaves the loop, which logs it and passes over the entry.
        try
        {
            if (waiting.Count > 0)
            {
                if (FollowsDeferring is { } deferring)
                {
                    await deferring(parent.ParcelId, cancellationToken);
                }

                await DeferFollowsAsync(project, parent.ParcelId, waiting, cancellationToken);
            }
        }
        finally
        {
            await EndTriggersAsync(project, parent, parcel, candidate, cancellationToken);
        }
    }

    /// <summary>
    /// Every published trigger on a flow run's end that fires in the project and matches <paramref name="candidate"/>, the
    /// end of <paramref name="parent"/> on <paramref name="parcel"/>, creates a parcel of its target's type, derived from
    /// that parcel and that flow run, and starts its target's flow on it. The claims on the ended flow run's parcel were
    /// weighed before the follows were, and gate the triggers alike; whether that parcel is closed or holds another live
    /// flow run does not, since the parcel a trigger creates is a new one. No trigger acts on the ended flow run's own
    /// parcel. A trigger refused, or failing for any other reason, is logged and the next one goes on.
    /// </summary>
    private async Task EndTriggersAsync(string project, FlowRun parent, Parcel parcel, TriggerEvent candidate, CancellationToken cancellationToken)
    {
        string cause = $"the end of flow run {Ids.Short(parent.Id)}";
        foreach (ResolvedTrigger trigger in await MatchingTriggersAsync(project, candidate, cancellationToken))
        {
            try
            {
                CreateParcelTarget target = trigger.Content.Target.CreateParcel
                    ?? throw new DomainException(
                        $"It acts on the parcel the event names, and a trigger on a flow run's end only creates a parcel; starting a flow on parcel {Ids.Short(parcel.Id)} is a flow's follow.");
                await CreateAsync(
                    trigger, target, project, TriggerKey.ForFlowRunEnd(trigger.Key, parent.Id), candidate.Fields, new DerivedFrom(parcel.Id, parent.Id), cause, cancellationToken);
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                LogTriggerRefused(trigger, cause, refused);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                LogTriggerFailed(trigger, cause, failure);
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="flowName"/> as a follower of <paramref name="parent"/>, unless that
    /// flow run already has a follower of the flow. True when the parcel holds a live flow run, so the
    /// follow has to wait for its release. <paramref name="fromQueue"/> says the follow comes from the
    /// parcel's follow queue rather than straight from the end, which decides only the test hook awaited before the start.
    /// </summary>
    private async Task<bool> FollowAsync(string project, FlowRun parent, string flowName, bool fromQueue, CancellationToken cancellationToken)
    {
        if (await FollowedAsync(project, parent, flowName, cancellationToken))
        {
            return false;
        }

        if (await HasLiveFlowRunAsync(project, parent.ParcelId, cancellationToken))
        {
            return true;
        }

        Task starting = fromQueue
            ? DeferredFollowStarting?.Invoke(new DeferredFollowStart(project, parent.ParcelId, parent.Id, flowName), cancellationToken) ?? Task.CompletedTask
            : DirectFollowStarting?.Invoke(new DirectFollowStart(project, parent.ParcelId, parent.Id, flowName), cancellationToken) ?? Task.CompletedTask;
        await starting;

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

    /// <summary>Whether any follow waits on the parcel for a release.</summary>
    private async Task<bool> HasDeferredFollowsAsync(string project, Guid parcelId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(project);
        DeferredFollows? deferred = await session.LoadAsync<DeferredFollows>(parcelId, cancellationToken);
        return deferred?.Pending.Count > 0;
    }

    /// <summary>Whether <paramref name="parent"/> already has a follower of <paramref name="flowName"/> on its parcel.</summary>
    private async Task<bool> FollowedAsync(string project, FlowRun parent, string flowName, CancellationToken cancellationToken)
    {
        IReadOnlyList<FlowRun> existing = await flowRuns.ListAsync(LoopPrincipal, project, parent.ParcelId, cancellationToken);
        return existing.Any(f => f.ParentFlowRunId == parent.Id && DefinitionResolution.SameName(f.FlowName, flowName));
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
    /// A release of the parcel's live flow run: the parcel's follow queue proceeds. Any node's release frees the
    /// parcel: a follow waits here only once this node's claims let it start, so whichever node recorded the
    /// release, the follow is this node's own.
    /// </summary>
    private async Task StartDeferredFollowsAsync(string project, Guid parcelId, long sequence, CancellationToken cancellationToken)
    {
        DeferredFollows? deferred;
        await using (IQuerySession session = store.QuerySession(project))
        {
            deferred = await session.LoadAsync<DeferredFollows>(parcelId, cancellationToken);
        }

        if (deferred is null || !await TriggersAsync(project, parcelId, own: true, sequence, "a release of its live flow run", cancellationToken))
        {
            return;
        }

        await ProceedAsync(project, parcelId, deferred, cancellationToken);
    }

    /// <summary>
    /// At start, once the loop has handled the record up to the head it read: every follow queue whose parcel is
    /// free proceeds as it does after a release, so a queue a release never came to start again, as one left by
    /// a daemon that failed a follow's start without taking it out, does not wait forever. A queue whose parcel
    /// has a live flow run waits for that flow run's release. One whose parcel holds back entries, or whose claims
    /// do not let this node start a flow run, starts nothing and is not held back, since there is no entry to
    /// hold: a later release, or the next start, finds it again.
    /// </summary>
    private async Task ResumeFollowQueuesAsync(CancellationToken cancellationToken)
    {
        foreach (string project in await ProjectsAsync(cancellationToken))
        {
            IReadOnlyList<DeferredFollows> queues;
            await using (IQuerySession session = store.QuerySession(project))
            {
                queues = await session.Query<DeferredFollows>().ToListAsync(cancellationToken);
            }

            foreach (DeferredFollows queue in queues.Where(q => q.Pending.Count > 0))
            {
                try
                {
                    await ResumeFollowQueueAsync(project, queue, cancellationToken);
                }
                catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogError(
                        failure, "The trigger loop could not resume the follow queue of parcel {Parcel} in project {Project} at start.",
                        Ids.Short(queue.Id), project);
                }
            }
        }
    }

    private async Task ResumeFollowQueueAsync(string project, DeferredFollows queue, CancellationToken cancellationToken)
    {
        if (await HasLiveFlowRunAsync(project, queue.Id, cancellationToken))
        {
            return;
        }

        if (await HasHeldBackAsync(project, queue.Id, cancellationToken))
        {
            logger.LogInformation(
                "Parcel {Parcel} in project {Project} holds back entries, so its follow queue starts nothing at start.",
                Ids.Short(queue.Id), project);
            return;
        }

        if (await StandingAsync(project, queue.Id, cancellationToken) is { } standing && !ClaimDecider.Triggers(standing, ownEvent: true))
        {
            logger.LogInformation(
                "Parcel {Parcel} in project {Project} is not this node's to start a flow run on, so its follow queue starts nothing at start.",
                Ids.Short(queue.Id), project);
            return;
        }

        logger.LogInformation(
            "Parcel {Parcel} in project {Project} has no live flow run, so its follow queue proceeds at start.",
            Ids.Short(queue.Id), project);
        await ProceedAsync(project, queue.Id, queue, cancellationToken);
    }

    /// <summary>
    /// Starts the follows waiting on the parcel, oldest first, until one has to wait for a release
    /// again, which the first one started makes the next do. A follow leaves the list once it has
    /// started, was already started, was refused, or failed to start for another reason, which is
    /// logged and passed over as a refusal is. A stop leaves the list as it was.
    /// </summary>
    private async Task ProceedAsync(string project, Guid parcelId, DeferredFollows deferred, CancellationToken cancellationToken)
    {
        List<PendingFollow> remaining = [.. deferred.Pending];
        while (remaining.Count > 0)
        {
            PendingFollow next = remaining[0];
            try
            {
                FlowRun parent = await flowRuns.ShowAsync(LoopPrincipal, project, next.ParentFlowRunId, cancellationToken)
                    ?? throw new DomainException($"No flow run {Ids.Short(next.ParentFlowRunId)} in project '{project}'.");
                if (await FollowAsync(project, parent, next.Flow, fromQueue: true, cancellationToken))
                {
                    break;
                }
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                LogFollowRefused(next.Flow, next.ParentFlowRunId, refused);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(
                    failure,
                    "Flow '{Flow}' follows the end of flow run {FlowRun}, but its start failed, so it leaves the parcel's follow queue and the next follow goes on.",
                    next.Flow, Ids.Short(next.ParentFlowRunId));
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

    /// <summary>
    /// An external event. One this node recorded creates, for every subscription of its project that matches it, the
    /// subscription's parcel and starts its flow, and for every trigger that fires in the project, matches it, and
    /// creates a parcel, the target's parcel and a flow run of the target's flow; one another node recorded creates no
    /// parcel here. Whichever node recorded it, every trigger that matches it and acts on the event's parcel starts its
    /// flow there where the parcel's claims let this node, weighed once for the event (<see cref="EventParcelAsync"/>).
    /// An event handled again because it was held back on its parcel's claim creates nothing: its creations were
    /// decided when it was first read, and only its starts on the parcel waited, with its resume of a flow run waiting
    /// for it on the parcel (<see cref="AwaitingAsync"/>), which is weighed under the same claims. Subscriptions fire
    /// before the triggers are resolved, and a subscription that fails for any reason is logged, so neither keeps the other from acting.
    /// </summary>
    private async Task ExternalAsync(ExternalEventRecorded external, bool own, long sequence, CancellationToken cancellationToken)
    {
        string project = external.Project;
        bool heldBackBefore = replaying is Guid parcel && parcel == external.ParcelId;
        bool creates = own && !heldBackBefore;
        if (creates)
        {
            await SubscriptionsAsync(external, cancellationToken);
        }

        IReadOnlyList<ResolvedTrigger> matching = await MatchingTriggersAsync(project, new TriggerEvent(external.Kind, external.Fields), cancellationToken);
        if (creates)
        {
            string cause = $"external event {Ids.Short(external.EventId)}";
            foreach (ResolvedTrigger trigger in matching)
            {
                if (trigger.Content.Target.CreateParcel is not { } target)
                {
                    continue;
                }

                try
                {
                    await CreateAsync(
                        trigger, target, project, TriggerKey.ForEvent(trigger.Key, external.EventId), external.Fields, null, cause, cancellationToken);
                }
                catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
                {
                    LogTriggerRefused(trigger, cause, refused);
                }
                catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
                {
                    LogTriggerFailed(trigger, cause, failure);
                }
            }
        }
        else if (!own)
        {
            logger.LogInformation(
                "External event {Event} of kind '{Kind}' in project {Project} was recorded by another node, so it creates no parcel here.",
                Ids.Short(external.EventId), external.Kind, project);
        }

        ResolvedTrigger[] onParcel = [.. matching.Where(t => t.Content.Target.EventParcel is not null)];
        Guid? awaiting = await AwaitingAsync(external, sequence, cancellationToken);
        if (onParcel.Length > 0 || awaiting is not null)
        {
            await EventParcelAsync(external, onParcel, awaiting, own, sequence, cancellationToken);
        }
    }

    /// <summary>
    /// The principal a resume for the external event <paramref name="eventId"/> acts as: the Agent role, named for the
    /// event by the id every node gives it, so the flow run's record names the event that resumed it, not a person.
    /// </summary>
    public static Principal ResumerFor(Guid eventId) => new($"external-event:{eventId}", Role.Agent);

    /// <summary>
    /// The live flow run on the parcel <paramref name="external"/> names that its own wait-for-external-event step
    /// paused, waiting for an event of this kind, before the event's entry at <paramref name="sequence"/>; null when
    /// the event names no parcel or none awaits it. A failure to read it is logged and read as none, so it never keeps
    /// the event's triggers from acting.
    /// </summary>
    private async Task<Guid?> AwaitingAsync(ExternalEventRecorded external, long sequence, CancellationToken cancellationToken)
    {
        if (external.ParcelId is not Guid parcelId)
        {
            return null;
        }

        try
        {
            return await flowRuns.AwaitingEventAsync(
                LoopPrincipal, external.Project, parcelId, external.Kind, sequence, ResumerFor(external.EventId).Id, cancellationToken);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                failure, "Reading whether a flow run on parcel {Parcel} awaits external event {Event} failed, so the event resumes nothing.",
                Ids.Short(parcelId), Ids.Short(external.EventId));
            return null;
        }
    }

    /// <summary>
    /// Resumes <paramref name="flowRunId"/> for <paramref name="external"/>, once the parcel's claims let this node, as the
    /// event's resumer (<see cref="ResumerFor"/>). The flow run service decides again inside the write whether the event is
    /// the one the step awaits, so a handling again, a pause since, or a resume since writes nothing. A refusal or a failure
    /// is logged, and the event's triggers on the parcel go on.
    /// </summary>
    private async Task ResumeAwaitingAsync(ExternalEventRecorded external, Guid flowRunId, long sequence, CancellationToken cancellationToken)
    {
        try
        {
            FlowRun? resumed = await flowRuns.ResumeOnEventAsync(
                ResumerFor(external.EventId), external.Project, flowRunId, external.EventId, external.Kind, sequence, cancellationToken);
            if (resumed is not null)
            {
                logger.LogInformation(
                    "External event {Event} of kind '{Kind}' resumed flow run {FlowRun} on parcel {Parcel} in project {Project}, whose step waits for it.",
                    Ids.Short(external.EventId), external.Kind, Ids.Short(flowRunId), Ids.Short(resumed.ParcelId), external.Project);
            }
        }
        catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "External event {Event} did not resume flow run {FlowRun}: {Rule}", Ids.Short(external.EventId), Ids.Short(flowRunId), refused.Message);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                failure, "Resuming flow run {FlowRun} for external event {Event} failed.", Ids.Short(flowRunId), Ids.Short(external.EventId));
        }
    }

    /// <summary>
    /// The flow run <paramref name="awaiting"/>, when given, is resumed for the event, and then the triggers among
    /// <paramref name="triggers"/>, each acting on the event's parcel, start their flows on it. The
    /// parcel's claims are weighed first, once for the event, as a binding's are (<see cref="TriggersAsync"/>): on a parcel
    /// this node holds, whichever node recorded the event; on an unclaimed one, only for an event this node recorded; on
    /// one another node's claim wins, never; and while this node's own claim is not yet held, the event is held back and
    /// handled again once it is held or lost. Only then is each trigger checked, and one refused is logged with its reason
    /// and keeps nothing to start later.
    /// </summary>
    private async Task EventParcelAsync(
        ExternalEventRecorded external, IReadOnlyList<ResolvedTrigger> triggers, Guid? awaiting, bool own, long sequence, CancellationToken cancellationToken)
    {
        string project = external.Project;
        string cause = $"external event {Ids.Short(external.EventId)}";
        if (external.ParcelId is not Guid parcelId)
        {
            foreach (ResolvedTrigger trigger in triggers)
            {
                LogTriggerRefused(trigger, cause, new DomainException(
                    $"It acts on the parcel the event names, and event {Ids.Short(external.EventId)} of kind '{external.Kind}' names no parcel."));
            }

            return;
        }

        if (!await TriggersAsync(project, parcelId, own, sequence, cause, cancellationToken))
        {
            return;
        }

        if (awaiting is Guid flowRunId)
        {
            await ResumeAwaitingAsync(external, flowRunId, sequence, cancellationToken);
        }

        foreach (ResolvedTrigger trigger in triggers)
        {
            try
            {
                await StartOnEventParcelAsync(trigger, external, parcelId, cancellationToken);
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                LogTriggerRefused(trigger, cause, refused);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                LogTriggerFailed(trigger, cause, failure);
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="trigger"/>'s flow on <paramref name="parcelId"/>, the parcel <paramref name="external"/> names,
    /// as a principal naming the trigger and the event, unless the parcel already has a flow run that principal started, live
    /// or ended, on this node or another. The parcel must be of the event's project, open, free of a live flow run, and of a
    /// type the target's flow, a published flow of the project, accepts.
    /// </summary>
    private async Task StartOnEventParcelAsync(ResolvedTrigger trigger, ExternalEventRecorded external, Guid parcelId, CancellationToken cancellationToken)
    {
        string project = external.Project;
        Principal principal = new(TriggerKey.ForEvent(trigger.Key, external.EventId), Role.Member);
        Parcel parcel = await parcels.ShowAsync(LoopPrincipal, project, parcelId, cancellationToken)
            ?? throw new DomainException(
                $"It acts on the parcel the event names, and event {Ids.Short(external.EventId)} names parcel {Ids.Short(parcelId)}, which is no parcel of project '{project}' on this node.");
        IReadOnlyList<FlowRun> existing = await flowRuns.ListAsync(LoopPrincipal, project, parcel.Id, cancellationToken);
        if (existing.Any(f => string.Equals(f.StartedBy, principal.Id, StringComparison.Ordinal)))
        {
            logger.LogInformation(
                "Trigger {Trigger} already started a flow run on parcel {Parcel} for event {Event}, so it starts none again.",
                trigger.Key, Ids.Short(parcel.Id), Ids.Short(external.EventId));
            return;
        }

        if (parcel.IsClosed)
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is closed ({parcel.Resolution}), and no flow run starts on a closed parcel.");
        }

        if (parcel.LiveFlowRunId is Guid live)
        {
            throw new DomainException(
                $"Parcel {Ids.Short(parcel.Id)} has flow run {Ids.Short(live)} live, and a parcel has at most one live flow run; nothing is kept to start later.");
        }

        string flowName = trigger.Content.Target.EventParcel?.Flow
            ?? throw new DomainException("Its target names no flow to start on the parcel the event names.");
        Flow flow = await PublishedFlowAsync(project, flowName, cancellationToken);
        RequireAccepts(flow, parcel.TypeName);
        await StartAsync(principal, project, parcel.Id, flow.Name, $"trigger {trigger.Key}", cancellationToken);
    }

    /// <summary>
    /// Creates <paramref name="target"/>'s parcel, recording <paramref name="key"/>, which names the trigger and the cause,
    /// as its origin and, when <paramref name="derivedFrom"/> is given, a derived-from relation in the same save, and starts
    /// the target's flow on it, both as a Member-role principal whose id is <paramref name="key"/>. Before anything is
    /// created, the target's type must resolve in the project, its flow be a published flow of the project that accepts
    /// the type, and <paramref name="fields"/> carry the field the target titles the parcel with, within a parcel title's
    /// cap; the value is never shortened. When the project already has a parcel recording that origin, from this node or
    /// another, nothing is created; when this node created it and it has no flow run, as after a stop between the
    /// creation and the start, the target's flow starts on it, whatever the trigger's hourly cap. Otherwise a trigger at its
    /// hourly cap in the project creates nothing (<see cref="WithinCapAsync"/>).
    /// </summary>
    private async Task CreateAsync(
        ResolvedTrigger trigger,
        CreateParcelTarget target,
        string project,
        string key,
        IReadOnlyDictionary<string, string> fields,
        DerivedFrom? derivedFrom,
        string cause,
        CancellationToken cancellationToken)
    {
        if (target is not { ParcelType: { } typeName, TitleField: { } titleField, Flow: { } flowName })
        {
            throw new DomainException("Its target names no parcel type, title field, or flow, so it creates nothing.");
        }

        var principal = new Principal(key, Role.Member);
        if (await parcels.FindByOriginAsync(LoopPrincipal, project, key, cancellationToken) is { } found)
        {
            await StartOnFoundAsync(trigger, principal, project, found, flowName, cause, cancellationToken);
            return;
        }

        if (!await WithinCapAsync(trigger, project, cause, cancellationToken))
        {
            return;
        }

        ParcelType type = await parcels.ResolveTypeAsync(LoopPrincipal, project, typeName, cancellationToken)
            ?? throw new DomainException($"Its target's parcel type '{typeName}' does not resolve in project '{project}', so no parcel was created.");
        Flow flow = await PublishedFlowAsync(project, flowName, cancellationToken);
        RequireAccepts(flow, type.Name);
        if (!fields.TryGetValue(titleField, out string? title))
        {
            throw new DomainException(
                $"It titles its parcel with the field '{titleField}', which {cause} does not carry, so no parcel was created.");
        }

        int length = title.Trim().Length;
        if (length > ParcelCaps.TitleLength)
        {
            throw new DomainException(
                $"It titles its parcel with the field '{titleField}' of {cause}, whose value is {length} characters long, and a parcel's title holds at most {ParcelCaps.TitleLength}; the value is not shortened, so no parcel was created and no flow run started.");
        }

        Parcel parcel = await parcels.AddAsync(principal, project, type.Name, title, null, key, derivedFrom, cancellationToken);
        logger.LogInformation(
            "Trigger {Trigger} created parcel {Parcel} of type '{Type}' in project {Project} for {Cause}.",
            trigger.Key, Ids.Short(parcel.Id), type.Name, project, cause);
        await StartAsync(principal, project, parcel.Id, flow.Name, $"trigger {trigger.Key}", cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="trigger"/> may create a parcel in the project for <paramref name="cause"/> under the hourly
    /// cap it carries now: it may while fewer parcels than its cap that it created there count toward it at this node's
    /// time (<see cref="TriggerCapCount"/>). An event over the cap is dropped with a warning and nothing is kept to create
    /// it later, since keeping it would queue without limit what the cap exists to stop.
    /// </summary>
    private async Task<bool> WithinCapAsync(ResolvedTrigger trigger, string project, string cause, CancellationToken cancellationToken)
    {
        int cap = trigger.Content.HourlyCap
            ?? throw new DomainException("It creates parcels and carries no hourly cap, so it creates none.");
        int counted = await TriggerCapCount.CountAsync(store, project, trigger.Definition.Name, clock.GetUtcNow(), cancellationToken);
        if (counted < cap)
        {
            return true;
        }

        logger.LogWarning(
            "Trigger {Trigger} in project {Project} is at its hourly cap of {Cap}, having created {Counted} parcel(s) there in the hour before, so {Cause} is dropped: it creates no parcel and starts no flow run, and is not kept to create one later.",
            trigger.Key, project, cap, counted, cause);
        return false;
    }

    /// <summary>
    /// A parcel the trigger already created for the cause: its flow starts on it only when this node created it and it has
    /// no flow run yet, so a handling again starts it once and a parcel another node created is that node's to start.
    /// </summary>
    private async Task StartOnFoundAsync(
        ResolvedTrigger trigger, Principal principal, string project, Parcel found, string flowName, string cause, CancellationToken cancellationToken)
    {
        if (!await CreatedHereAsync(project, found.Id, cancellationToken))
        {
            logger.LogInformation(
                "Trigger {Trigger} creates nothing for {Cause}: another node already created parcel {Parcel} for it.",
                trigger.Key, cause, Ids.Short(found.Id));
            return;
        }

        if ((await flowRuns.ListAsync(LoopPrincipal, project, found.Id, cancellationToken)).Count > 0)
        {
            return;
        }

        await StartAsync(principal, project, found.Id, flowName, $"trigger {trigger.Key}", cancellationToken);
    }

    /// <summary>Whether this node recorded the parcel's creation, rather than the import appending it from another node's line.</summary>
    private async Task<bool> CreatedHereAsync(string project, Guid parcelId, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, 1, token: cancellationToken);
        if (events.Count == 0)
        {
            return false;
        }

        IReadOnlyDictionary<Guid, LedgerLine> lines = await ImportedEvents.LinesAsync(session, [events[0].Id], cancellationToken);
        return !lines.TryGetValue(events[0].Id, out LedgerLine? line) || line.Origin == identity.NodeId;
    }

    /// <summary>
    /// The published triggers resolution returns for the project that match <paramref name="candidate"/>, nearest scope first
    /// by name, so of a project-scope and a module-scope trigger of one name only the project's is among them. A module-scope
    /// trigger is among them only in a project its module is switched on for. One whose content cannot be read is logged and left out.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedTrigger>> MatchingTriggersAsync(string project, TriggerEvent candidate, CancellationToken cancellationToken)
    {
        IReadOnlyList<Definition> resolved = await definitions.ResolveAllAsync(project, DefinitionKind.Trigger, cancellationToken);
        if (resolved.Count == 0)
        {
            return [];
        }

        Project? registered = await ProjectService.FindAsync(store, project, cancellationToken);
        List<ResolvedTrigger> matching = [];
        foreach (Definition definition in resolved)
        {
            if (definition.Scope == DefinitionScope.Module && !(definition.Module is { } module && registered?.IsSwitchedOn(module) == true))
            {
                continue;
            }

            TriggerDefinition content;
            try
            {
                content = TriggerDefinition.FromJson(definition.Content);
            }
            catch (DomainException unreadable)
            {
                logger.LogWarning(
                    "Trigger '{Trigger}' of project {Project} cannot be read, so it fires on nothing: {Rule}",
                    definition.Name, project, unreadable.Message);
                continue;
            }

            if (Trigger.ForDefinition(content).Matches(candidate))
            {
                matching.Add(new ResolvedTrigger(definition, content));
            }
        }

        return matching;
    }

    /// <summary>The flow resolution returns for the project by <paramref name="flowName"/>, or a refusal saying it is not a published flow of it.</summary>
    private async Task<Flow> PublishedFlowAsync(string project, string flowName, CancellationToken cancellationToken)
    {
        Definition published = await definitions.ResolveAsync(project, DefinitionKind.Flow, flowName, cancellationToken)
            ?? throw new DomainException($"Its target's flow '{flowName}' is not a published flow of project '{project}', so nothing was created or started.");
        return Flow.FromJson(published.Content);
    }

    private static void RequireAccepts(Flow flow, string typeName)
    {
        if (!flow.Accepts.Any(type => DefinitionResolution.SameName(type, typeName)))
        {
            throw new DomainException(
                $"Its target's flow '{flow.Name}' accepts {string.Join(", ", flow.Accepts)}, not the parcel type '{typeName}', so nothing was created or started.");
        }
    }

    private void LogTriggerRefused(ResolvedTrigger trigger, string cause, DomainException refused) =>
        logger.LogWarning("Trigger {Trigger} on {Cause} was refused: {Rule}", trigger.Key, cause, refused.Message);

    private void LogTriggerFailed(ResolvedTrigger trigger, string cause, Exception failure) =>
        logger.LogError(failure, "Trigger {Trigger} on {Cause} failed, and the next trigger goes on.", trigger.Key, cause);

    /// <summary>
    /// Every subscription of the event's project that matches it creates its parcel and starts its flow. A subscription
    /// refused, or failing for any other reason, is logged and the next one goes on, and a failure to list the
    /// subscriptions is logged too, so none of it keeps the event's triggers from acting.
    /// </summary>
    private async Task SubscriptionsAsync(ExternalEventRecorded external, CancellationToken cancellationToken)
    {
        TriggerEvent candidate = new(external.Kind, external.Fields);
        IReadOnlyList<Subscription> subscriptions;
        try
        {
            subscriptions = await triggers.ListSubscriptionsAsync(LoopPrincipal, external.Project, cancellationToken);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(
                failure,
                "Listing the subscriptions of project {Project} for event {Event} failed, so no subscription fires on it and its triggers go on.",
                external.Project, Ids.Short(external.EventId));
            return;
        }

        foreach (Subscription subscription in subscriptions.Where(s => s.Trigger.Matches(candidate)))
        {
            try
            {
                if (SubscriptionFiring is { } firing)
                {
                    await firing(subscription, cancellationToken);
                }

                await FireAsync(subscription, external, cancellationToken);
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Subscription {Subscription} on event {Event} was refused: {Rule}",
                    Ids.Short(subscription.Id), Ids.Short(external.EventId), refused.Message);
            }
            catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(
                    failure,
                    "Subscription {Subscription} on event {Event} failed, and the next subscription goes on.",
                    Ids.Short(subscription.Id), Ids.Short(external.EventId));
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

    /// <summary>
    /// A parcel's entry into a stage as the loop weighs it: the entry's project, parcel, the parcel's type and the stage it
    /// entered, the id the entry's origin gave it, its version in the parcel's stream, the flow run live on the parcel at
    /// the entry, the parcels the parcel listed as blocking it then, whether this node recorded it, and its global sequence.
    /// </summary>
    private sealed record StageEntry(
        string Project,
        Guid ParcelId,
        string TypeName,
        string Stage,
        Guid EntryId,
        long Version,
        Guid? LiveAtEntry,
        IReadOnlyList<Guid> BlockedBy,
        bool Own,
        long Sequence);

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
