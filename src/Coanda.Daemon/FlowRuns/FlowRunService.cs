using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Coanda.Daemon.SystemSteps;
using Fisher;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// The daemon's side of every flow run command and query: pass the registry for the method's own
/// entry, fetch for writing, ask <see cref="FlowRunDecider"/>, append, save. A new flow run takes the
/// flow that resolution returns for the project by name; an existing flow run keeps the version
/// whose hash it recorded.
/// </summary>
/// <remarks>
/// A parcel has at most one live flow run, and that holds when two starts race: a start appends
/// <see cref="ParcelFlowRunAttached"/> to the parcel stream in the same unit of work that starts the
/// flow run stream, through <c>FetchForWriting</c>, so the parcel stream's expected version guards it.
/// The loser of a race fails the version check, its flow run stream is never written, and it is
/// refused with the rule. Ending and stopping append <see cref="ParcelFlowRunReleased"/> the same way,
/// and the end of a flow run whose flow declares a closing resolution appends the parcel's
/// <see cref="ParcelClosed"/> in that same save, running the gates the service is given, so the end,
/// the release, and the close are recorded together or not at all.
/// A resume hands the flow run to the <see cref="IFlowRunAdvancer"/>, when the service has one, so a
/// resumed flow run advances with no further command; a pause tells it to end the agent step the flow run is
/// running, and a stop to end the agent step or system step it is running and remove its worktree, once the
/// pause or stop is saved. Every write to an existing flow run, its end
/// and its stop among them, reads the flow run again when another write landed between its read and
/// its save, and decides again from what it read, so it refuses only with the rule that the flow run
/// as it now stands breaks. Beside the registry check, a principal resolved from a session token
/// (<see cref="Principal.Session"/>) records only on the flow run it is bound to, in that flow run's project,
/// while that flow run, as the write fetched it, still expects its launch, and starts no flow run; every
/// other principal, the runner's and the trigger loop's among them, passes that check untouched.
/// </remarks>
public sealed class FlowRunService(IDocumentStore store, TimeProvider clock, IFlowRunAdvancer? advancer = null, GateSet? gates = null)
{
    /// <summary>How many times a write reads the flow run and decides before a run of concurrent writes refuses it.</summary>
    private const int Attempts = 5;

    private readonly DefinitionResolver definitions = new(store);

    /// <summary>The gates a close at a flow run's end runs, the shipped set when the service is given none.</summary>
    private readonly GateSet gates = gates ?? GateSet.Shipped;

    public async Task<FlowRun> StartAsync(Principal principal, string project, Guid parcelId, string flowName, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.start", principal);
        RefuseSessionStart(principal);
        string tenant = Tenants.RequireProject(project);
        Definition published = await definitions.ResolveAsync(tenant, DefinitionKind.Flow, flowName, cancellationToken)
            ?? throw new DomainException($"'{flowName}' is not a published flow of project '{tenant}'; coa flow list names the ones that are.");
        Flow flow = Flow.FromJson(published.Content);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var parcelStream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(parcelId, tenant);
        (FlowRunStarted started, ParcelFlowRunAttached attached) =
            FlowRunDecider.Start(flow, published.VersionNumber, parcel, principal.Id, clock.GetUtcNow());
        parcelStream.AppendOne(attached);
        session.Events.StartStream<FlowRun>(started.FlowRunId, started);
        await SaveGuardedAsync(session, ParcelChanged(parcel.Id), cancellationToken);

        return FlowRun.Create(started);
    }

    /// <summary>
    /// Starts the flow resolution returns for the project by <paramref name="flowName"/> as the next
    /// link of <paramref name="parentFlowRunId"/>'s chain, on the parent's parcel. The flow's follow
    /// must match how the parent ended, and every rule of a start holds too.
    /// </summary>
    public async Task<FlowRun> FollowAsync(
        Principal principal, string project, Guid parentFlowRunId, string flowName, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.start", principal);
        RefuseSessionStart(principal);
        string tenant = Tenants.RequireProject(project);
        Definition published = await definitions.ResolveAsync(tenant, DefinitionKind.Flow, flowName, cancellationToken)
            ?? throw new DomainException($"'{flowName}' is not a published flow of project '{tenant}'; coa flow list names the ones that are.");
        Flow flow = Flow.FromJson(published.Content);

        await using IDocumentSession session = store.LightweightSession(tenant);
        FlowRun parent = await session.LoadAsync<FlowRun>(parentFlowRunId, cancellationToken) ?? throw FlowRunNotFound(parentFlowRunId, tenant);
        var parcelStream = await session.Events.FetchForWriting<Parcel>(parent.ParcelId, cancellationToken);
        Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(parent.ParcelId, tenant);
        (FlowRunStarted started, ParcelFlowRunAttached attached) =
            FlowRunDecider.Follow(flow, published.VersionNumber, parcel, parent, principal.Id, clock.GetUtcNow());
        parcelStream.AppendOne(attached);
        session.Events.StartStream<FlowRun>(started.FlowRunId, started);
        await SaveGuardedAsync(session, ParcelChanged(parcel.Id), cancellationToken);

        return FlowRun.Create(started);
    }

    public Task<FlowRun> StartStepAsync(Principal principal, string project, Guid flowRunId, string step, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.step", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.StartStep(flow, flowRun, step, clock.GetUtcNow()),
            (flowRun, started) => flowRun.Apply(started),
            cancellationToken);

    /// <summary>Counts one more spawn for the resumed agent step <paramref name="step"/>, on its current visit.</summary>
    public Task<FlowRun> RespawnAsync(Principal principal, string project, Guid flowRunId, string step, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.respawn", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.Respawn(flow, flowRun, step, clock.GetUtcNow()),
            (flowRun, respawned) => flowRun.Apply(respawned),
            cancellationToken);

    /// <summary>
    /// Records that the agent step <paramref name="step"/> launched its agent, with the agent's content
    /// hash and model spec, on the spawn its visit last counted.
    /// </summary>
    public Task<FlowRun> SpawnedAsync(
        Principal principal, string project, Guid flowRunId, string step, string agentHash, ModelSpec model, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.spawn", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.Spawn(flow, flowRun, step, agentHash, model, clock.GetUtcNow()),
            (flowRun, spawned) => flowRun.Apply(spawned),
            cancellationToken);

    /// <summary>Records the session the executor returned for the agent step <paramref name="step"/>'s latest launch.</summary>
    public Task<FlowRun> SessionOpenedAsync(
        Principal principal, string project, Guid flowRunId, string step, Guid sessionId, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.session", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.OpenSession(flow, flowRun, step, sessionId, clock.GetUtcNow()),
            (flowRun, opened) => flowRun.Apply(opened),
            cancellationToken);

    /// <summary>Records the answer the session <paramref name="sessionId"/> gave, when the visit still expects that session.</summary>
    public Task<FlowRun> RecordAnswerAsync(
        Principal principal,
        string project,
        Guid flowRunId,
        string step,
        string agent,
        Guid sessionId,
        string agentHash,
        ModelSpec model,
        JsonElement answer,
        Measurement measurement,
        CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.answer", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.Answer(flow, flowRun, step, agent, sessionId, agentHash, model, answer, measurement, clock.GetUtcNow()),
            (flowRun, answered) => flowRun.Apply(answered),
            cancellationToken);

    /// <summary>
    /// Records what the agent step <paramref name="step"/> spent in the session <paramref name="sessionId"/>
    /// when neither its answer nor its pause was recorded, so the flow run's cost counts it.
    /// </summary>
    public Task<FlowRun> RecordUsageAsync(
        Principal principal, string project, Guid flowRunId, string step, string agent, Guid sessionId, Measurement usage, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.usage", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.RecordUsage(flow, flowRun, step, agent, sessionId, usage, clock.GetUtcNow()),
            (flowRun, recorded) => flowRun.Apply(recorded),
            cancellationToken);

    public Task<FlowRun> RecordResultAsync(
        Principal principal, string project, Guid flowRunId, string step, string systemStep, JsonElement result, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.result", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.Complete(flow, flowRun, step, systemStep, result, clock.GetUtcNow()),
            (flowRun, completed) => flowRun.Apply(completed),
            cancellationToken);

    /// <summary>
    /// Records the id the system step <paramref name="step"/> is dispatched under, minted for this
    /// dispatch, before the step is called; the flow run it returns holds that id on its current visit.
    /// </summary>
    public Task<FlowRun> DispatchedAsync(Principal principal, string project, Guid flowRunId, string step, CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.dispatch", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.Dispatch(flow, flowRun, step, clock.GetUtcNow()),
            (flowRun, dispatched) => flowRun.Apply(dispatched),
            cancellationToken);

    /// <summary>
    /// Records the outcome of the dispatch <paramref name="dispatchId"/> of the flow run's current
    /// system step: a result, which the flow run routes on, or a pause with its reason and lever. It
    /// is refused unless the flow run is running and the dispatch is its current visit's, holds no
    /// outcome yet, and has not been replaced by a resume. Once the outcome is saved the flow run is
    /// handed to the <see cref="IFlowRunAdvancer"/>, when the service has one, which routes on it as
    /// it would on the same answer given at the dispatch; the record does not wait for that. A Module
    /// principal records an outcome only in a project its module is switched on for, and only under a
    /// dispatch it holds (<see cref="FlowRunDecider.RequireHeldBy"/>).
    /// </summary>
    public async Task<FlowRun> RecordOutcomeAsync(
        Principal principal, string project, Guid flowRunId, Guid dispatchId, SystemStepAnswer outcome, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.outcome", principal);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, Tenants.RequireProject(project), cancellationToken);
        FlowRun recorded = outcome switch
        {
            SystemStepAnswer.Done done => await WriteAsync(
                "flow-run.outcome", principal, project, flowRunId,
                (flow, flowRun) =>
                {
                    RequireModuleHold(principal, flowRun, dispatchId);
                    return FlowRunDecider.CompleteDispatch(flow, flowRun, dispatchId, done.Result, clock.GetUtcNow());
                },
                (flowRun, completed) => flowRun.Apply(completed),
                cancellationToken),
            SystemStepAnswer.Paused paused => await WriteAsync(
                "flow-run.outcome", principal, project, flowRunId,
                (flow, flowRun) =>
                {
                    RequireModuleHold(principal, flowRun, dispatchId);
                    return FlowRunDecider.PauseDispatch(flow, flowRun, dispatchId, paused.Reason, paused.Lever, principal.Id, clock.GetUtcNow());
                },
                (flowRun, recordedPause) => flowRun.Apply(recordedPause),
                cancellationToken),
            _ => throw new DomainException(
                $"Dispatch {Ids.Short(dispatchId)} is recorded with its outcome, a result or a pause; running is no outcome."),
        };

        _ = advancer?.RequestAdvance(project.Trim(), recorded.Id);
        return recorded;
    }

    /// <summary>Pauses a running flow run and, once the pause is saved, ends the agent step it was running.</summary>
    public async Task<FlowRun> PauseAsync(Principal principal, string project, Guid flowRunId, string reason, string lever, CancellationToken cancellationToken)
    {
        FlowRun paused = await PauseAsync(principal, project, flowRunId, reason, lever, usage: null, session: null, cancellationToken);
        advancer?.EndAgentStep(paused.Id);
        return paused;
    }

    /// <summary>
    /// Pauses a flow run, recording the <paramref name="usage"/> an agent step that paused without an
    /// answer reported. When the agent step's <paramref name="session"/> is given, the visit must
    /// still expect it.
    /// </summary>
    public Task<FlowRun> PauseAsync(
        Principal principal,
        string project,
        Guid flowRunId,
        string reason,
        string lever,
        Measurement? usage,
        Guid? session,
        CancellationToken cancellationToken) =>
        WriteAsync(
            "flow-run.pause", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.Pause(flow, flowRun, reason, lever, principal.Id, clock.GetUtcNow(), usage, session),
            (flowRun, paused) => flowRun.Apply(paused),
            cancellationToken);

    public async Task<FlowRun> ResumeAsync(Principal principal, string project, Guid flowRunId, string? note, CancellationToken cancellationToken)
    {
        FlowRun resumed = await WriteAsync(
            "flow-run.resume", principal, project, flowRunId,
            (flow, flowRun) => FlowRunDecider.Resume(flow, flowRun, note, principal.Id, clock.GetUtcNow()),
            (flowRun, recorded) => flowRun.Apply(recorded),
            cancellationToken);

        // The advance runs on the daemon's own workers; the resume does not wait for it.
        _ = advancer?.RequestAdvance(project.Trim(), resumed.Id);
        return resumed;
    }

    public Task<FlowRun> EndAsync(Principal principal, string project, Guid flowRunId, string outcome, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.end", principal);
        string tenant = Tenants.RequireProject(project);
        RequireBoundFlowRun(principal, tenant, flowRunId);

        return DecidingAgainAsync(
            async token =>
            {
                await using IDocumentSession session = store.LightweightSession(tenant);
                var flowRunStream = await session.Events.FetchForWriting<FlowRun>(flowRunId, token);
                FlowRun flowRun = flowRunStream.Aggregate ?? throw FlowRunNotFound(flowRunId, tenant);
                RequireBoundLaunch(principal, flowRun);
                Flow flow = await RequireStartedUnderFlowAsync(tenant, flowRun, token);
                DateTimeOffset now = clock.GetUtcNow();
                (FlowRunEnded ended, ParcelFlowRunReleased released) = FlowRunDecider.End(flow, flowRun, outcome, principal.Id, now);
                flowRunStream.AppendOne(ended);
                IEventStream<Parcel> parcelStream = await AppendReleaseAsync(session, flowRun, released, token);
                Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(flowRun.ParcelId, tenant);

                // A flow that closes its parcel closes it in the same save as the end and the
                // release, decided against the parcel as the release leaves it; a refusal saves none of them.
                if (flow.Closes is not null)
                {
                    ParcelType type = await definitions.ResolveParcelTypeByHashAsync(tenant, parcel.TypeHash, token)
                        ?? throw new DomainException(
                            $"The '{parcel.TypeName}' type version {parcel.TypeHash} that parcel {Ids.Short(parcel.Id)} was created under is no longer held, so flow run {Ids.Short(flowRun.Id)} cannot close it at its end.");
                    ParcelClosed? closed = FlowRunDecider.CloseOnEnd(
                        flow, flowRun, type, gates, parcel, FlowRunAdvancer.PrincipalFor(flowRun.Id).Id, now);
                    if (closed is not null)
                    {
                        parcelStream.AppendOne(closed);
                    }
                }

                await session.SaveChangesAsync(token);
                flowRun.Apply(ended);
                return flowRun;
            },
            FlowRunOrParcelChanged(flowRunId),
            cancellationToken);
    }

    /// <summary>
    /// Stops a live flow run and, once the stop is saved, ends the agent step or system step it was running and
    /// has a dispatch it holds stopped. The stop is marked as being saved until the advancer has been told
    /// of it, so the module feed holds its delivery until then. It needs no flow, so a flow run whose flow
    /// is no longer held can still be stopped.
    /// </summary>
    public async Task<FlowRun> StopAsync(Principal principal, string project, Guid flowRunId, string reason, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.stop", principal);
        string tenant = Tenants.RequireProject(project);
        RequireBoundFlowRun(principal, tenant, flowRunId);

        using IDisposable? marked = advancer?.Stopping(flowRunId);
        FlowRun stopped = await DecidingAgainAsync(
            async token =>
            {
                await using IDocumentSession session = store.LightweightSession(tenant);
                var flowRunStream = await session.Events.FetchForWriting<FlowRun>(flowRunId, token);
                FlowRun flowRun = flowRunStream.Aggregate ?? throw FlowRunNotFound(flowRunId, tenant);
                RequireBoundLaunch(principal, flowRun);
                (FlowRunStopped recorded, ParcelFlowRunReleased released) = FlowRunDecider.Stop(flowRun, reason, principal.Id, clock.GetUtcNow());
                flowRunStream.AppendOne(recorded);
                await ReleaseAsync(session, flowRun, released, token);

                flowRun.Apply(recorded);
                return flowRun;
            },
            FlowRunOrParcelChanged(flowRunId),
            cancellationToken);

        advancer?.Stopped(tenant, stopped.Id);
        return stopped;
    }

    /// <summary>The project's flow runs, or one parcel's when <paramref name="parcelId"/> is given, oldest first.</summary>
    public async Task<IReadOnlyList<FlowRun>> ListAsync(Principal principal, string project, Guid? parcelId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.list", principal);
        string tenant = Tenants.RequireProject(project);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);

        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<FlowRun> flowRuns = parcelId is Guid parcel
            ? await session.Query<FlowRun>().Where(f => f.ParcelId == parcel).ToListAsync(cancellationToken)
            : await session.Query<FlowRun>().ToListAsync(cancellationToken);
        return [.. flowRuns.OrderBy(f => f.StartedAt).ThenBy(f => f.Id)];
    }

    public async Task<FlowRun?> ShowAsync(Principal principal, string project, Guid flowRunId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.show", principal);
        string tenant = Tenants.RequireProject(project);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);

        await using IQuerySession session = store.QuerySession(tenant);
        return await session.LoadAsync<FlowRun>(flowRunId, cancellationToken);
    }

    /// <summary>
    /// Every flow run of <paramref name="member"/>'s chain, the root and the member included, oldest
    /// first. A chain runs on one parcel, so it is read from that parcel's flow runs.
    /// </summary>
    public async Task<IReadOnlyList<FlowRun>> ChainOfAsync(Principal principal, string project, FlowRun member, CancellationToken cancellationToken)
    {
        IReadOnlyList<FlowRun> onParcel = await ListAsync(principal, project, member.ParcelId, cancellationToken);
        return [.. onParcel.Where(f => f.RootFlowRunId == member.RootFlowRunId)];
    }

    /// <summary>The flow version the flow run started under, which is the one it keeps.</summary>
    public async Task<Flow> FlowOfAsync(Principal principal, string project, FlowRun flowRun, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.show", principal);
        string tenant = Tenants.RequireProject(project);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        return await RequireStartedUnderFlowAsync(tenant, flowRun, cancellationToken);
    }

    /// <summary>
    /// Every live flow run on the node, in every project, as the project and the flow run id: the
    /// flow run each parcel names as its live one. A Module principal gets those of the projects its
    /// module is switched on for.
    /// </summary>
    public async Task<IReadOnlyList<(string Project, Guid FlowRunId)>> ListLiveAsync(Principal principal, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flow-run.list", principal);

        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Parcel> parcels = await session.Query<Parcel>().AnyTenant().ToListAsync(cancellationToken);
        IReadOnlyList<Project> projects = principal.Role == Role.Module
            ? await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken)
            : [];
        return [.. parcels
            .Where(p => p.LiveFlowRunId is not null)
            .Where(p => ModuleScope.IsSwitchedOn(principal, projects.FirstOrDefault(r => string.Equals(r.Name, p.Project, StringComparison.Ordinal))))
            .Select(p => (p.Project, p.LiveFlowRunId.GetValueOrDefault()))];
    }

    private async Task<FlowRun> WriteAsync<TEvent>(
        string entry,
        Principal principal,
        string project,
        Guid flowRunId,
        Func<Flow, FlowRun, TEvent> decide,
        Action<FlowRun, TEvent> apply,
        CancellationToken cancellationToken)
        where TEvent : notnull
    {
        CommandRegistry.Require(entry, principal);
        string tenant = Tenants.RequireProject(project);
        RequireBoundFlowRun(principal, tenant, flowRunId);

        return await DecidingAgainAsync(
            async token =>
            {
                await using IDocumentSession session = store.LightweightSession(tenant);
                var stream = await session.Events.FetchForWriting<FlowRun>(flowRunId, token);
                FlowRun flowRun = stream.Aggregate ?? throw FlowRunNotFound(flowRunId, tenant);
                RequireBoundLaunch(principal, flowRun);
                Flow flow = await RequireStartedUnderFlowAsync(tenant, flowRun, token);
                TEvent recorded = decide(flow, flowRun);
                stream.AppendOne(recorded);
                await session.SaveChangesAsync(token);

                apply(flowRun, recorded);
                return flowRun;
            },
            $"Flow run {Ids.Short(flowRunId)} kept changing while this was being recorded, so nothing was. Read the flow run again and retry.",
            cancellationToken);
    }

    /// <summary>
    /// A session principal never starts a flow run, by a start or a follow, since a flow run it starts is
    /// never the one it is bound to. Every other principal, the trigger loop's among them, passes.
    /// </summary>
    private static void RefuseSessionStart(Principal principal)
    {
        if (principal.Session is { } bound)
        {
            throw new DomainException($"{BoundRule(bound)}; a flow run it starts is never its own, so it starts none.");
        }
    }

    /// <summary>
    /// A session principal writes only to the flow run it is bound to, in that flow run's project; checked
    /// before the flow run is read. Every other principal passes.
    /// </summary>
    private static void RequireBoundFlowRun(Principal principal, string tenant, Guid flowRunId)
    {
        if (principal.Session is { } bound
            && (!string.Equals(bound.Project, tenant, StringComparison.Ordinal) || bound.FlowRunId != flowRunId))
        {
            throw new DomainException($"{BoundRule(bound)}; flow run {Ids.Short(flowRunId)} in project '{tenant}' is not that flow run.");
        }
    }

    /// <summary>
    /// A session principal writes only while the flow run, as this write fetched it, still expects the
    /// launch it is bound to; so a call whose token resolved before its launch ended records nothing
    /// once it has. Every other principal passes.
    /// </summary>
    private static void RequireBoundLaunch(Principal principal, FlowRun flowRun)
    {
        if (principal.Session is { } bound && !flowRun.ExpectsLaunch(bound.LaunchId))
        {
            throw new DomainException($"{BoundRule(bound)}; that launch has ended, so it records nothing more.");
        }
    }

    /// <summary>
    /// A Module principal writes under a dispatch only when its module holds it, as the flow run this
    /// write fetched stands. Every other principal passes.
    /// </summary>
    private static void RequireModuleHold(Principal principal, FlowRun flowRun, Guid dispatchId)
    {
        if (principal.Role == Role.Module)
        {
            FlowRunDecider.RequireHeldBy(flowRun, dispatchId, principal.Module ?? string.Empty);
        }
    }

    private static string BoundRule(SessionBinding bound) =>
        $"A session's principal records only on flow run {Ids.Short(bound.FlowRunId)} in project '{bound.Project}', the flow run it is bound to, while its launch {Ids.Short(bound.LaunchId)} runs";

    /// <summary>
    /// Reads, decides, and saves through <paramref name="attempt"/>, and when another write landed on
    /// what it read before its save, does it all again from a fresh read: the decision is made again
    /// against the flow run as it now stands, so the write is refused only by a rule that flow run breaks.
    /// Only a run of <see cref="Attempts"/> concurrent writes refuses it with <paramref name="refusal"/>.
    /// </summary>
    private static async Task<FlowRun> DecidingAgainAsync(
        Func<CancellationToken, Task<FlowRun>> attempt, string refusal, CancellationToken cancellationToken)
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

    private static async Task ReleaseAsync(IDocumentSession session, FlowRun flowRun, ParcelFlowRunReleased released, CancellationToken cancellationToken)
    {
        await AppendReleaseAsync(session, flowRun, released, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Appends the release to the parcel's stream, guarded by its version, when the parcel names the
    /// flow run as its live one, and returns the parcel's stream, its aggregate as the release leaves it, unsaved.
    /// </summary>
    private static async Task<IEventStream<Parcel>> AppendReleaseAsync(
        IDocumentSession session, FlowRun flowRun, ParcelFlowRunReleased released, CancellationToken cancellationToken)
    {
        var parcelStream = await session.Events.FetchForWriting<Parcel>(flowRun.ParcelId, cancellationToken);
        Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(flowRun.ParcelId, session.TenantId);

        // A parcel names only its live flow run; releasing one it does not name would clear another's hold.
        if (parcel.LiveFlowRunId == flowRun.Id)
        {
            parcelStream.AppendOne(released);
            parcel.Apply(released);
        }

        return parcelStream;
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

    private static string FlowRunOrParcelChanged(Guid flowRunId) =>
        $"Flow run {Ids.Short(flowRunId)} or its parcel kept changing while this was being recorded, so nothing was. Read them again and retry.";

    private static string ParcelChanged(Guid parcelId) =>
        $"A parcel has at most one live flow run; parcel {Ids.Short(parcelId)} or its flow run changed while this was being recorded, so nothing was. Read them again and retry.";

    /// <summary>A flow run keeps the flow version it started under; a newer version applies to new flow runs only.</summary>
    private async Task<Flow> RequireStartedUnderFlowAsync(string project, FlowRun flowRun, CancellationToken cancellationToken)
    {
        Definition? found = await definitions.ResolveByHashAsync(project, DefinitionKind.Flow, flowRun.FlowHash, cancellationToken);
        return found is null
            ? throw new DomainException($"The '{flowRun.FlowName}' flow version {flowRun.FlowHash} that flow run {Ids.Short(flowRun.Id)} started under is no longer held.")
            : Flow.FromJson(found.Content);
    }

    private static DomainException ParcelNotFound(Guid parcelId, string project) =>
        new($"No parcel {Ids.Short(parcelId)} in project '{project}'.");

    private static DomainException FlowRunNotFound(Guid flowRunId, string project) =>
        new($"No flow run {Ids.Short(flowRunId)} in project '{project}'.");
}
