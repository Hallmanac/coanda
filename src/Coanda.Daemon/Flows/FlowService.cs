using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Flows.Events;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Flows;

/// <summary>
/// The daemon's side of every flow command and query: pass the registry for the method's own
/// entry, fetch for writing, ask <see cref="FlowDecider"/>, append, save. A new flow takes the
/// contour that resolution returns for the project by name; an existing flow keeps the version
/// whose hash it recorded.
/// </summary>
/// <remarks>
/// A parcel has at most one live flow, and that holds when two starts race: a start appends
/// <see cref="ParcelFlowAttached"/> to the parcel stream in the same unit of work that starts the
/// flow stream, through <c>FetchForWriting</c>, so the parcel stream's expected version guards it.
/// The loser of a race fails the version check, its flow stream is never written, and it is
/// refused with the rule. Ending and stopping append <see cref="ParcelFlowReleased"/> the same way.
/// A resume hands the flow to the <see cref="IFlowAdvancer"/>, when the service has one, so a
/// resumed flow advances with no further command; a pause tells it to end the judgment the flow is
/// running, and a stop to end the judgment or action it is running and remove its worktree, once the
/// pause or stop is saved. Every write to an existing flow, its end
/// and its stop among them, reads the flow again when another write landed between its read and
/// its save, and decides again from what it read, so it refuses only with the rule that the flow
/// as it now stands breaks. Beside the registry check, a principal resolved from a session token
/// (<see cref="Principal.Session"/>) records only on the flow it is bound to, in that flow's project,
/// while that flow, as the write fetched it, still expects its launch, and starts no flow; every
/// other principal, the runner's and the trigger loop's among them, passes that check untouched.
/// </remarks>
public sealed class FlowService(IDocumentStore store, TimeProvider clock, IFlowAdvancer? advancer = null)
{
    /// <summary>How many times a write reads the flow and decides before a run of concurrent writes refuses it.</summary>
    private const int Attempts = 5;

    private readonly DefinitionResolver definitions = new(store);

    public async Task<Flow> StartAsync(Principal principal, string project, Guid parcelId, string contourName, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.start", principal);
        RefuseSessionStart(principal);
        string tenant = Tenants.RequireProject(project);
        Definition published = await definitions.ResolveAsync(tenant, DefinitionKind.Contour, contourName, cancellationToken)
            ?? throw new DomainException($"'{contourName}' is not a published contour of project '{tenant}'; coa contour list names the ones that are.");
        Contour contour = Contour.FromJson(published.Content);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var parcelStream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(parcelId, tenant);
        (FlowStarted started, ParcelFlowAttached attached) =
            FlowDecider.Start(contour, published.VersionNumber, parcel, principal.Id, clock.GetUtcNow());
        parcelStream.AppendOne(attached);
        session.Events.StartStream<Flow>(started.FlowId, started);
        await SaveGuardedAsync(session, ParcelChanged(parcel.Id), cancellationToken);

        return Flow.Create(started);
    }

    /// <summary>
    /// Starts the contour resolution returns for the project by <paramref name="contourName"/> as the next
    /// link of <paramref name="parentFlowId"/>'s chain, on the parent's parcel. The contour's follow
    /// must match how the parent ended, and every rule of a start holds too.
    /// </summary>
    public async Task<Flow> FollowAsync(
        Principal principal, string project, Guid parentFlowId, string contourName, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.start", principal);
        RefuseSessionStart(principal);
        string tenant = Tenants.RequireProject(project);
        Definition published = await definitions.ResolveAsync(tenant, DefinitionKind.Contour, contourName, cancellationToken)
            ?? throw new DomainException($"'{contourName}' is not a published contour of project '{tenant}'; coa contour list names the ones that are.");
        Contour contour = Contour.FromJson(published.Content);

        await using IDocumentSession session = store.LightweightSession(tenant);
        Flow parent = await session.LoadAsync<Flow>(parentFlowId, cancellationToken) ?? throw FlowNotFound(parentFlowId, tenant);
        var parcelStream = await session.Events.FetchForWriting<Parcel>(parent.ParcelId, cancellationToken);
        Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(parent.ParcelId, tenant);
        (FlowStarted started, ParcelFlowAttached attached) =
            FlowDecider.Follow(contour, published.VersionNumber, parcel, parent, principal.Id, clock.GetUtcNow());
        parcelStream.AppendOne(attached);
        session.Events.StartStream<Flow>(started.FlowId, started);
        await SaveGuardedAsync(session, ParcelChanged(parcel.Id), cancellationToken);

        return Flow.Create(started);
    }

    public Task<Flow> StartStepAsync(Principal principal, string project, Guid flowId, string step, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow.step", principal, project, flowId,
            (contour, flow) => FlowDecider.StartStep(contour, flow, step, clock.GetUtcNow()),
            (flow, started) => flow.Apply(started),
            cancellationToken);

    /// <summary>Counts one more spawn for the resumed judgment <paramref name="step"/>, on its current visit.</summary>
    public Task<Flow> RespawnAsync(Principal principal, string project, Guid flowId, string step, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow.respawn", principal, project, flowId,
            (contour, flow) => FlowDecider.Respawn(contour, flow, step, clock.GetUtcNow()),
            (flow, respawned) => flow.Apply(respawned),
            cancellationToken);

    /// <summary>
    /// Records that the judgment <paramref name="step"/> launched its agent, with the agent's content
    /// hash and model spec, on the spawn its visit last counted.
    /// </summary>
    public Task<Flow> SpawnedAsync(
        Principal principal, string project, Guid flowId, string step, string agentHash, ModelSpec model, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow.spawn", principal, project, flowId,
            (contour, flow) => FlowDecider.Spawn(contour, flow, step, agentHash, model, clock.GetUtcNow()),
            (flow, spawned) => flow.Apply(spawned),
            cancellationToken);

    /// <summary>Records the session the executor returned for the judgment <paramref name="step"/>'s latest launch.</summary>
    public Task<Flow> SessionOpenedAsync(
        Principal principal, string project, Guid flowId, string step, Guid sessionId, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow.session", principal, project, flowId,
            (contour, flow) => FlowDecider.OpenSession(contour, flow, step, sessionId, clock.GetUtcNow()),
            (flow, opened) => flow.Apply(opened),
            cancellationToken);

    /// <summary>Records the answer the session <paramref name="sessionId"/> gave, when the visit still expects that session.</summary>
    public Task<Flow> RecordAnswerAsync(
        Principal principal,
        string project,
        Guid flowId,
        string step,
        string agent,
        Guid sessionId,
        string agentHash,
        ModelSpec model,
        JsonElement answer,
        Measurement measurement,
        CancellationToken cancellationToken) =>
        WriteAsync(
            "flow.answer", principal, project, flowId,
            (contour, flow) => FlowDecider.Answer(contour, flow, step, agent, sessionId, agentHash, model, answer, measurement, clock.GetUtcNow()),
            (flow, answered) => flow.Apply(answered),
            cancellationToken);

    /// <summary>
    /// Records what the judgment <paramref name="step"/> spent in the session <paramref name="sessionId"/>
    /// when neither its answer nor its pause was recorded, so the flow's cost counts it.
    /// </summary>
    public Task<Flow> RecordUsageAsync(
        Principal principal, string project, Guid flowId, string step, string agent, Guid sessionId, Measurement usage, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow.usage", principal, project, flowId,
            (contour, flow) => FlowDecider.RecordUsage(contour, flow, step, agent, sessionId, usage, clock.GetUtcNow()),
            (flow, recorded) => flow.Apply(recorded),
            cancellationToken);

    public Task<Flow> RecordResultAsync(
        Principal principal, string project, Guid flowId, string step, string action, JsonElement result, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow.result", principal, project, flowId,
            (contour, flow) => FlowDecider.Complete(contour, flow, step, action, result, clock.GetUtcNow()),
            (flow, completed) => flow.Apply(completed),
            cancellationToken);

    /// <summary>Pauses a running flow and, once the pause is saved, ends the judgment it was running.</summary>
    public async Task<Flow> PauseAsync(Principal principal, string project, Guid flowId, string reason, string lever, CancellationToken cancellationToken)
    {
        Flow paused = await PauseAsync(principal, project, flowId, reason, lever, usage: null, session: null, cancellationToken);
        advancer?.EndJudgment(paused.Id);
        return paused;
    }

    /// <summary>
    /// Pauses a flow, recording the <paramref name="usage"/> a judgment that paused without an
    /// answer reported. When the judgment's <paramref name="session"/> is given, the visit must
    /// still expect it.
    /// </summary>
    public Task<Flow> PauseAsync(
        Principal principal,
        string project,
        Guid flowId,
        string reason,
        string lever,
        Measurement? usage,
        Guid? session,
        CancellationToken cancellationToken) =>
        WriteAsync(
            "flow.pause", principal, project, flowId,
            (contour, flow) => FlowDecider.Pause(contour, flow, reason, lever, principal.Id, clock.GetUtcNow(), usage, session),
            (flow, paused) => flow.Apply(paused),
            cancellationToken);

    public async Task<Flow> ResumeAsync(Principal principal, string project, Guid flowId, string? note, CancellationToken cancellationToken)
    {
        Flow resumed = await WriteAsync(
            "flow.resume", principal, project, flowId,
            (contour, flow) => FlowDecider.Resume(contour, flow, note, principal.Id, clock.GetUtcNow()),
            (flow, recorded) => flow.Apply(recorded),
            cancellationToken);

        // The advance runs on the daemon's own workers; the resume does not wait for it.
        _ = advancer?.RequestAdvance(project.Trim(), resumed.Id);
        return resumed;
    }

    public Task<Flow> EndAsync(Principal principal, string project, Guid flowId, string outcome, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.end", principal);
        string tenant = Tenants.RequireProject(project);
        RequireBoundFlow(principal, tenant, flowId);

        return DecidingAgainAsync(
            async token =>
            {
                await using IDocumentSession session = store.LightweightSession(tenant);
                var flowStream = await session.Events.FetchForWriting<Flow>(flowId, token);
                Flow flow = flowStream.Aggregate ?? throw FlowNotFound(flowId, tenant);
                RequireBoundLaunch(principal, flow);
                Contour contour = await RequireStartedUnderContourAsync(tenant, flow, token);
                (FlowEnded ended, ParcelFlowReleased released) = FlowDecider.End(contour, flow, outcome, principal.Id, clock.GetUtcNow());
                flowStream.AppendOne(ended);
                await ReleaseAsync(session, flow, released, token);

                flow.Apply(ended);
                return flow;
            },
            FlowOrParcelChanged(flowId),
            cancellationToken);
    }

    /// <summary>
    /// Stops a live flow and, once the stop is saved, ends the judgment or action it was running and
    /// has its worktree removed. It needs no contour, so a flow whose contour is no longer held can
    /// still be stopped.
    /// </summary>
    public async Task<Flow> StopAsync(Principal principal, string project, Guid flowId, string reason, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.stop", principal);
        string tenant = Tenants.RequireProject(project);
        RequireBoundFlow(principal, tenant, flowId);

        Flow stopped = await DecidingAgainAsync(
            async token =>
            {
                await using IDocumentSession session = store.LightweightSession(tenant);
                var flowStream = await session.Events.FetchForWriting<Flow>(flowId, token);
                Flow flow = flowStream.Aggregate ?? throw FlowNotFound(flowId, tenant);
                RequireBoundLaunch(principal, flow);
                (FlowStopped recorded, ParcelFlowReleased released) = FlowDecider.Stop(flow, reason, principal.Id, clock.GetUtcNow());
                flowStream.AppendOne(recorded);
                await ReleaseAsync(session, flow, released, token);

                flow.Apply(recorded);
                return flow;
            },
            FlowOrParcelChanged(flowId),
            cancellationToken);

        advancer?.Stopped(tenant, stopped.Id);
        return stopped;
    }

    /// <summary>The project's flows, or one parcel's when <paramref name="parcelId"/> is given, oldest first.</summary>
    public async Task<IReadOnlyList<Flow>> ListAsync(Principal principal, string project, Guid? parcelId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.list", principal);
        string tenant = Tenants.RequireProject(project);

        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Flow> flows = parcelId is Guid parcel
            ? await session.Query<Flow>().Where(f => f.ParcelId == parcel).ToListAsync(cancellationToken)
            : await session.Query<Flow>().ToListAsync(cancellationToken);
        return [.. flows.OrderBy(f => f.StartedAt).ThenBy(f => f.Id)];
    }

    public async Task<Flow?> ShowAsync(Principal principal, string project, Guid flowId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.show", principal);
        string tenant = Tenants.RequireProject(project);

        await using IQuerySession session = store.QuerySession(tenant);
        return await session.LoadAsync<Flow>(flowId, cancellationToken);
    }

    /// <summary>
    /// Every flow of <paramref name="member"/>'s chain, the root and the member included, oldest
    /// first. A chain runs on one parcel, so it is read from that parcel's flows.
    /// </summary>
    public async Task<IReadOnlyList<Flow>> ChainOfAsync(Principal principal, string project, Flow member, CancellationToken cancellationToken)
    {
        IReadOnlyList<Flow> onParcel = await ListAsync(principal, project, member.ParcelId, cancellationToken);
        return [.. onParcel.Where(f => f.RootFlowId == member.RootFlowId)];
    }

    /// <summary>The contour version the flow started under, which is the one it keeps.</summary>
    public async Task<Contour> ContourOfAsync(Principal principal, string project, Flow flow, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.show", principal);
        return await RequireStartedUnderContourAsync(Tenants.RequireProject(project), flow, cancellationToken);
    }

    /// <summary>
    /// Every live flow on the node, in every project, as the project and the flow id: the
    /// flow each parcel names as its live one.
    /// </summary>
    public async Task<IReadOnlyList<(string Project, Guid FlowId)>> ListLiveAsync(Principal principal, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow.list", principal);

        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Parcel> parcels = await session.Query<Parcel>().AnyTenant().ToListAsync(cancellationToken);
        return [.. parcels
            .Where(p => p.LiveFlowId is not null)
            .Select(p => (p.Project, p.LiveFlowId.GetValueOrDefault()))];
    }

    private async Task<Flow> WriteAsync<TEvent>(
        string entry,
        Principal principal,
        string project,
        Guid flowId,
        Func<Contour, Flow, TEvent> decide,
        Action<Flow, TEvent> apply,
        CancellationToken cancellationToken)
        where TEvent : notnull
    {
        CommandRegistry.Require(entry, principal);
        string tenant = Tenants.RequireProject(project);
        RequireBoundFlow(principal, tenant, flowId);

        return await DecidingAgainAsync(
            async token =>
            {
                await using IDocumentSession session = store.LightweightSession(tenant);
                var stream = await session.Events.FetchForWriting<Flow>(flowId, token);
                Flow flow = stream.Aggregate ?? throw FlowNotFound(flowId, tenant);
                RequireBoundLaunch(principal, flow);
                Contour contour = await RequireStartedUnderContourAsync(tenant, flow, token);
                TEvent recorded = decide(contour, flow);
                stream.AppendOne(recorded);
                await session.SaveChangesAsync(token);

                apply(flow, recorded);
                return flow;
            },
            $"Flow {Ids.Short(flowId)} kept changing while this was being recorded, so nothing was. Read the flow again and retry.",
            cancellationToken);
    }

    /// <summary>
    /// A session principal never starts a flow, by a start or a follow, since a flow it starts is
    /// never the one it is bound to. Every other principal, the trigger loop's among them, passes.
    /// </summary>
    private static void RefuseSessionStart(Principal principal)
    {
        if (principal.Session is { } bound)
        {
            throw new DomainException($"{BoundRule(bound)}; a flow it starts is never its own, so it starts none.");
        }
    }

    /// <summary>
    /// A session principal writes only to the flow it is bound to, in that flow's project; checked
    /// before the flow is read. Every other principal passes.
    /// </summary>
    private static void RequireBoundFlow(Principal principal, string tenant, Guid flowId)
    {
        if (principal.Session is { } bound
            && (!string.Equals(bound.Project, tenant, StringComparison.Ordinal) || bound.FlowId != flowId))
        {
            throw new DomainException($"{BoundRule(bound)}; flow {Ids.Short(flowId)} in project '{tenant}' is not that flow.");
        }
    }

    /// <summary>
    /// A session principal writes only while the flow, as this write fetched it, still expects the
    /// launch it is bound to; so a call whose token resolved before its launch ended records nothing
    /// once it has. Every other principal passes.
    /// </summary>
    private static void RequireBoundLaunch(Principal principal, Flow flow)
    {
        if (principal.Session is { } bound && !flow.ExpectsLaunch(bound.LaunchId))
        {
            throw new DomainException($"{BoundRule(bound)}; that launch has ended, so it records nothing more.");
        }
    }

    private static string BoundRule(SessionBinding bound) =>
        $"A session's principal records only on flow {Ids.Short(bound.FlowId)} in project '{bound.Project}', the flow it is bound to, while its launch {Ids.Short(bound.LaunchId)} runs";

    /// <summary>
    /// Reads, decides, and saves through <paramref name="attempt"/>, and when another write landed on
    /// what it read before its save, does it all again from a fresh read: the decision is made again
    /// against the flow as it now stands, so the write is refused only by a rule that flow breaks.
    /// Only a run of <see cref="Attempts"/> concurrent writes refuses it with <paramref name="refusal"/>.
    /// </summary>
    private static async Task<Flow> DecidingAgainAsync(
        Func<CancellationToken, Task<Flow>> attempt, string refusal, CancellationToken cancellationToken)
    {
        for (int tried = 1; ; tried++)
        {
            try
            {
                return await attempt(cancellationToken);
            }
            catch (ConcurrencyException) when (tried < Attempts)
            {
                // Another write landed between this read and its save; read again and decide again.
            }
            catch (ConcurrencyException)
            {
                throw new DomainException(refusal);
            }
        }
    }

    private static async Task ReleaseAsync(IDocumentSession session, Flow flow, ParcelFlowReleased released, CancellationToken cancellationToken)
    {
        var parcelStream = await session.Events.FetchForWriting<Parcel>(flow.ParcelId, cancellationToken);
        Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(flow.ParcelId, session.TenantId);

        // A parcel names only its live flow; releasing one it does not name would clear another's hold.
        if (parcel.LiveFlowId == flow.Id)
        {
            parcelStream.AppendOne(released);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Saves, turning a failed version check into a refusal that says what moved.</summary>
    private static async Task SaveGuardedAsync(IDocumentSession session, string refusal, CancellationToken cancellationToken)
    {
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            throw new DomainException(refusal);
        }
    }

    private static string FlowOrParcelChanged(Guid flowId) =>
        $"Flow {Ids.Short(flowId)} or its parcel kept changing while this was being recorded, so nothing was. Read them again and retry.";

    private static string ParcelChanged(Guid parcelId) =>
        $"A parcel has at most one live flow; parcel {Ids.Short(parcelId)} or its flow changed while this was being recorded, so nothing was. Read them again and retry.";

    /// <summary>A flow keeps the contour version it started under; a newer version applies to new flows only.</summary>
    private async Task<Contour> RequireStartedUnderContourAsync(string project, Flow flow, CancellationToken cancellationToken)
    {
        Definition? found = await definitions.ResolveByHashAsync(project, DefinitionKind.Contour, flow.ContourHash, cancellationToken);
        return found is null
            ? throw new DomainException($"The '{flow.ContourName}' contour version {flow.ContourHash} that flow {Ids.Short(flow.Id)} started under is no longer held.")
            : Contour.FromJson(found.Content);
    }

    private static DomainException ParcelNotFound(Guid parcelId, string project) =>
        new($"No parcel {Ids.Short(parcelId)} in project '{project}'.");

    private static DomainException FlowNotFound(Guid flowId, string project) =>
        new($"No flow {Ids.Short(flowId)} in project '{project}'.");
}
