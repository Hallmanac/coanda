using System.Globalization;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;
using Fisher;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// Advances live flow runs on the daemon's own workers: performs the current step through the
/// executor or an action, records its result on the flow run, asks the interpreter for the next
/// move, and applies it, until the flow run ends, pauses, or stops. At most one worker advances a
/// flow run; a request for a flow run already being advanced is folded into that worker, which reads
/// the flow run once more before it finishes. Before any step, a flow run deeper in its chain than its
/// flow's follow allows pauses; before a judgment, a flow run whose recorded cost has reached its flow's
/// budget, or whose chain's recorded cost has reached the budget of the flow its root runs, pauses.
/// Both checks run on every pass, so a resume of such a pause pauses again rather than overriding it.
/// A resumed judgment step spawns another agent, so it pauses once the flow run's spawns reach its
/// flow's spawn cap, whether the visit resumed had recorded an answer or paused without one.
/// A resume of a visit that recorded performs the step again as a new visit, so it pauses before
/// entering the step when every transition out of it has a visit cap below that visit; the
/// spawn-cap check runs first, so it answers when both bind.
/// </summary>
/// <remarks>
/// Every write is made as the flow run's own principal, of the Agent role, through a flow run service
/// of the runner's own: the service the daemon registers hands resumes to this runner, so the
/// runner cannot also take that one without a cycle. A daemon stop cancels the step in progress
/// and records no answer, result, or pause for it, leaving the flow run live and unpaused so the next
/// start carries it on. A judgment whose session the flow run already names keeps that session alive
/// across the stop, and the next start reattaches it and records its answer without launching the
/// agent again; one stopped before its session was recorded has the session stopped, and the next
/// start, finding no session to reattach, pauses the flow run rather than launch it again uncounted.
/// The next start first stops every session the executor holds that no running flow run waits on. A
/// cancellation counts as a daemon stop only when the daemon's own token is cancelled. A step
/// recorded before the stop but not yet routed is routed without being performed again. A
/// person's pause or stop, once saved, ends the judgment in progress at once, its session stopped
/// through the executor. A pause lets an action in progress run on; a stop cancels it, ending any
/// command it runs, and the action records nothing. When a flow run ends, or once its worker has
/// returned from the step it was performing when it was stopped, the flow run's worktree is removed; a
/// removal that fails is logged, and the daemon's next start sweeps what is left. Every judgment
/// that launched a session records what it spent: in its answer, in its pause, or, when neither
/// was recorded (a person paused or stopped the flow run, or its visit no longer expects the session),
/// in a record of its usage alone. An answer or a pause from a session the visit no longer expects,
/// because a resume came after its launch, does not pause the flow run: the resume's own pass performs
/// the step again. Each launch, once recorded and before its agent starts, is issued a session token
/// of its own (<see cref="SessionTokens"/>), handed to the executor in the spawn's
/// <see cref="SessionAccess"/>, and removed when the judgment ends, unless a daemon stop ended it:
/// the session then runs on, and the token it holds resolves again once the next start reattaches it.
/// </remarks>
public sealed class FlowRunAdvancer : IFlowRunAdvancer, IHostedService, IDisposable
{
    /// <summary>The principal that reads the node's live flow runs when the daemon starts.</summary>
    public static readonly Principal ScanPrincipal = new("coanda-runner", Role.Agent);

    private readonly FlowRunService flowRuns;
    private readonly ProjectService projects;
    private readonly ParcelService parcels;
    private readonly DefinitionService definitions;
    private readonly FlowActions actions;
    private readonly IAgentExecutor executor;
    private readonly CredentialProfiles profiles;
    private readonly ProjectClones clones;
    private readonly NodeHome home;
    private readonly SessionTokens sessionTokens;
    private readonly TimeProvider clock;
    private readonly TimeSpan judgmentLimit;
    private readonly ILogger<FlowRunAdvancer> logger;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Worker> workers = [];
    private readonly FlowRunWorkers flowRunWorkers;

    /// <summary>The flow runs whose session the start-up sweep left running for the flow run's first advance to reattach.</summary>
    private readonly HashSet<Guid> claimedAtStart = [];

    /// <summary>
    /// The launches this runner made whose judgment failed before it recorded an answer or a pause,
    /// so a reattach of one is not across a daemon restart.
    /// </summary>
    private readonly HashSet<Guid> launchedHere = [];
    private bool stopRequested;

    public FlowRunAdvancer(
        IDocumentStore store,
        ProjectService projects,
        ParcelService parcels,
        DefinitionService definitions,
        FlowActions actions,
        IAgentExecutor executor,
        CredentialProfiles profiles,
        ProjectClones clones,
        NodeHome home,
        SessionTokens sessionTokens,
        TimeProvider clock,
        FlowRunAdvancerOptions options,
        GateSet gates,
        FlowRunWorkers flowRunWorkers,
        ILogger<FlowRunAdvancer> logger)
    {
        // A flow run's end closes its parcel when its flow says so, running the gates the daemon runs.
        flowRuns = new FlowRunService(store, clock, advancer: null, gates);
        this.projects = projects;
        this.parcels = parcels;
        this.definitions = definitions;
        this.actions = actions;
        this.executor = executor;
        this.profiles = profiles;
        this.clones = clones;
        this.home = home;
        this.sessionTokens = sessionTokens;
        this.clock = clock;
        judgmentLimit = options.JudgmentLimit;
        this.flowRunWorkers = flowRunWorkers;
        this.logger = logger;
    }

    /// <summary>The principal every write made while advancing a flow run carries: the Agent role, named for the flow run.</summary>
    public static Principal PrincipalFor(Guid flowRunId) => new($"flow-run:{flowRunId}", Role.Agent);

    public Task RequestAdvance(string project, Guid flowRunId)
    {
        lock (gate)
        {
            if (stopRequested)
            {
                return Task.CompletedTask;
            }

            if (workers.TryGetValue(flowRunId, out Worker? running))
            {
                running.Again = true;
                return running.Idle;
            }

            var worker = new Worker();
            workers.Add(flowRunId, worker);
            worker.Idle = Task.Run(() => WorkAsync(project, flowRunId, worker, stopping.Token));
            flowRunWorkers.Started(flowRunId, worker.Idle);
            return worker.Idle;
        }
    }

    public void EndJudgment(Guid flowRunId)
    {
        lock (gate)
        {
            // Only the state changes here; the judgment's own continuations run on the pool, not on
            // the caller's thread, and the source is disposed only after it is no longer watched.
            if (workers.TryGetValue(flowRunId, out Worker? running) && running.Judging is { } judging)
            {
                _ = judging.CancelAsync();
            }
        }
    }

    public void Stopped(string project, Guid flowRunId)
    {
        lock (gate)
        {
            if (workers.TryGetValue(flowRunId, out Worker? running))
            {
                running.Stopping = true;
                if (running.Judging is { } judging)
                {
                    _ = judging.CancelAsync();
                }

                if (running.Acting is { } acting)
                {
                    _ = acting.CancelAsync();
                }
            }
        }

        // The worker reads the stopped flow run once it returns from its step, or a new one reads it now,
        // and either removes its worktree.
        _ = RequestAdvance(project, flowRunId);
    }

    /// <summary>
    /// Removes the worktree of every flow run an existing project clone lists that has ended or been
    /// stopped, as an end or a stop would have, leaving the worktrees of live flow runs as they are. The
    /// daemon runs it at start, before any live flow run is advanced; a sweep that fails is logged.
    /// </summary>
    public async Task SweepWorktreesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await clones.SweepAsync(
                async (project, flowRunId, token) =>
                    await flowRuns.ShowAsync(ScanPrincipal, project, flowRunId, token) is { Status: FlowRunStatus.Ended or FlowRunStatus.Stopped },
                cancellationToken);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(failure, "Sweeping the worktrees of ended and stopped flow runs failed; the daemon starts without it.");
        }
    }

    /// <summary>
    /// Requests an advance of every live flow run on the node; a paused one is read and left as it is.
    /// First it stops every session the executor lists that no running flow run is waiting on, so no
    /// agent a daemon stop left behind runs on with nobody to collect it. A flow run waits on a session
    /// when its current judgment visit launched since it was last resumed, has no answer, and names
    /// that session; its own advance reattaches it.
    /// </summary>
    public async Task AdvanceLiveAsync(CancellationToken cancellationToken)
    {
        await SweepSessionTokensAsync(cancellationToken);
        IReadOnlyList<(string Project, Guid FlowRunId)> live = await flowRuns.ListLiveAsync(ScanPrincipal, cancellationToken);
        await StopUnclaimedSessionsAsync(live, cancellationToken);
        foreach ((string project, Guid flowRunId) in live)
        {
            _ = RequestAdvance(project, flowRunId);
        }
    }

    /// <summary>
    /// Removes the stored session tokens whose launch its flow run no longer expects: ones a daemon stop
    /// kept for a flow run that was paused, stopped, or ended before this start, or whose removal failed.
    /// A sweep that fails is logged and the start goes on, since such a token resolves to nothing anyway.
    /// </summary>
    private async Task SweepSessionTokensAsync(CancellationToken cancellationToken)
    {
        try
        {
            int removed = await sessionTokens.SweepAsync(cancellationToken);
            if (removed > 0)
            {
                logger.LogInformation("Removed {Count} session tokens whose launch no flow run expects any more.", removed);
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(failure, "Sweeping the session tokens no launch is expected for failed; the daemon starts without it.");
        }
    }

    /// <summary>
    /// Removes the session tokens issued for <paramref name="launch"/> once its judgment has ended,
    /// unless a daemon stop ended it: then the session is left running for the next start to
    /// reattach, and its process still holds the token. A removal that fails is logged, and the next
    /// start's sweep removes what is left, since by then the flow run no longer expects the launch.
    /// </summary>
    private async Task EndSessionTokensAsync(Guid launch, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await sessionTokens.RevokeAsync(launch, cancellationToken);
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Removing the session tokens of launch {Launch} failed; the next start removes them.", Ids.Short(launch));
        }
    }

    /// <summary>
    /// Stops each session the executor lists that none of the <paramref name="live"/> flow runs is
    /// running and waiting on. A stop that fails is logged, and the start goes on: the session is
    /// one no flow run will collect, and failing the start over it would strand every flow run that can run.
    /// </summary>
    private async Task StopUnclaimedSessionsAsync(IReadOnlyList<(string Project, Guid FlowRunId)> live, CancellationToken cancellationToken)
    {
        var claimed = new HashSet<Guid>();
        foreach ((string project, Guid flowRunId) in live)
        {
            FlowRun? flowRun = await flowRuns.ShowAsync(PrincipalFor(flowRunId), project, flowRunId, cancellationToken);
            if (flowRun is { Status: FlowRunStatus.Running, Current: { AwaitsLaunchedSession: true, SessionId: Guid session } })
            {
                claimed.Add(session);
                lock (gate)
                {
                    claimedAtStart.Add(flowRunId);
                }
            }
        }

        IReadOnlyList<SessionHandle> held;
        try
        {
            held = await executor.ListAsync(cancellationToken);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(failure, "Listing the executor's sessions failed, so none was stopped at the start.");
            return;
        }

        foreach (SessionHandle unclaimed in held.Where(h => !claimed.Contains(h.Id)))
        {
            logger.LogInformation(
                "Stopping session {Session} of agent '{Agent}', which no running flow run is waiting on.", Ids.Short(unclaimed.Id), unclaimed.Agent);
            await StopSessionAsync(unclaimed);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Cancels every step in progress and waits for the workers to give up, recording no answer, result, or pause for those steps.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] running;
        lock (gate)
        {
            stopRequested = true;
            running = [.. workers.Values.Select(w => w.Idle)];
        }

        await stopping.CancelAsync();
        try
        {
            await Task.WhenAll(running).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The daemon stopped before {Count} flow run workers finished giving up their steps.", running.Length);
        }
    }

    public void Dispose() => stopping.Dispose();

    private async Task WorkAsync(string project, Guid flowRunId, Worker worker, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await AdvanceAsync(project, flowRunId, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // A daemon stop records no answer, result, or pause for the step in progress, leaving the
                // flow run live for the next start to carry on: it reattaches a judgment whose session the flow run
                // names rather than performing it again, and routes a step recorded but not yet routed.
            }
            catch (Exception failure)
            {
                logger.LogError(failure, "Advancing flow run {FlowRun} in project {Project} failed; it is left as it was recorded.", Ids.Short(flowRunId), project);
            }

            lock (gate)
            {
                if (!worker.Again || cancellationToken.IsCancellationRequested)
                {
                    workers.Remove(flowRunId);
                    flowRunWorkers.Returned(flowRunId);
                    return;
                }

                worker.Again = false;
            }
        }
    }

    private async Task AdvanceAsync(string project, Guid flowRunId, CancellationToken cancellationToken)
    {
        Principal principal = PrincipalFor(flowRunId);
        bool claimed;
        lock (gate)
        {
            claimed = claimedAtStart.Remove(flowRunId);
        }

        bool moving = true;
        while (moving)
        {
            FlowRun? flowRun = await flowRuns.ShowAsync(principal, project, flowRunId, cancellationToken);
            if (flowRun is { Status: FlowRunStatus.Ended or FlowRunStatus.Stopped })
            {
                await RemoveWorktreeAsync(project, flowRunId, cancellationToken);
                return;
            }

            if (flowRun is not { Status: FlowRunStatus.Running })
            {
                // Paused or stopped by a person after the start-up sweep left its session running,
                // and before any watch was there for that pause or stop to end.
                if (claimed && flowRun is not null)
                {
                    await EndClaimedSessionAsync(principal, project, flowRun, cancellationToken);
                }

                return;
            }

            claimed = false;

            try
            {
                moving = await StepAsync(principal, project, flowRun, cancellationToken);
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                await PauseOnRefusalAsync(principal, project, flowRun, refused, cancellationToken);
                return;
            }
        }
    }

    /// <summary>Takes the flow run one step on; true when it entered another step and is still running.</summary>
    private async Task<bool> StepAsync(Principal principal, string project, FlowRun flowRun, CancellationToken cancellationToken)
    {
        Flow flow = await flowRuns.FlowOfAsync(principal, project, flowRun, cancellationToken);
        FlowRunStepEntry current = flowRun.Current
            ?? throw new DomainException($"Flow run {Ids.Short(flowRun.Id)} has entered no step, so there is nothing to perform.");
        FlowStep step = flow.Step(current.Step)
            ?? throw new DomainException($"Flow '{flow.Name}' has no step '{current.Step}', so flow run {Ids.Short(flowRun.Id)} cannot perform it.");

        if (flow.Follow is { } follow && flowRun.Depth > follow.DepthCap)
        {
            await PauseAsync(
                principal, project, flowRun.Id,
                $"Flow run {Ids.Short(flowRun.Id)} is at depth {flowRun.Depth} of its chain, past the depth cap of {follow.DepthCap} on the follow of flow '{flow.Name}'; a flow run deeper than its follow's depth cap performs no step.",
                FlowRunLevers.DepthCapReached(flow.Name),
                cancellationToken);
            return false;
        }

        // A resumed judgment spawns another agent, whether its visit recorded or not, once its
        // counted spawn is used: not when the visit paused before it launched, and not when this
        // resume's spawn is already counted (a restart performing the resumed visit again). A
        // judgment the interpreter entered within the cap is performed once, so it is not checked.
        bool spawnsAgain = current.SpawnsAgainOnResume;
        if (spawnsAgain && flowRun.Spawns >= flow.SpawnCap)
        {
            await PauseAsync(
                principal, project, flowRun.Id,
                $"Flow '{flow.Name}' reached its spawn cap of {flow.SpawnCap}; performing judgment step '{step.Name}' again would spawn another agent.",
                FlowRunLevers.SpawnCapReached(flow.Name),
                cancellationToken);
            return false;
        }

        // A resume after this visit recorded (a pause the interpreter decided) performs the step
        // again as a new visit; a recording with no resume since is routed, never performed again.
        if (current.IsRecorded && current.ResumedAt is not null)
        {
            // A new visit no transition's visit cap allows would be performed only to pause on the
            // same cap, so it pauses before the step is entered, spawned, or acted.
            int visits = flowRun.Position.VisitsTo(step.Name);
            if (!Interpreter.CanFireAtNextVisit(step, visits))
            {
                await PauseAsync(
                    principal, project, flowRun.Id,
                    $"Step '{step.Name}' has a visit count of {visits}, and every transition out of it has a visit cap below {visits + 1}; performing it again could only pause on a visit cap.",
                    FlowRunLevers.VisitCapsSpent(step.Name),
                    cancellationToken);
                return false;
            }

            await flowRuns.StartStepAsync(principal, project, flowRun.Id, step.Name, cancellationToken);
            return true;
        }

        Recording? recorded = current.IsRecorded
            ? (current.Answer ?? current.Result) is JsonElement held ? new Recording(flowRun, held) : null
            : step.IsJudgment
                ? current.AwaitsLaunchedSession
                    ? await ReattachAsync(principal, project, flowRun, step, current, cancellationToken)
                    : await JudgeAsync(principal, project, flowRun, flow, step, spawnsAgain, cancellationToken)
                : await ActAsync(principal, project, flowRun, flow, step, current.ResumedAt is not null, cancellationToken);

        // Routed against the flow run as the recording left it, so a spawn counted on this pass is in the position.
        return recorded is Recording recording
            && await RouteAsync(principal, project, recording.FlowRun, flow, step, recording.Value, cancellationToken);
    }

    /// <summary>
    /// Stops the session a resumed visit still names, before the judgment launches another in the
    /// same working directory: one whose stop failed when the visit paused, say. The flow run names that
    /// session until the new launch is recorded, and the resume means nobody will collect it, so it
    /// is ended here rather than left running. A visit launched since its last resume never reaches
    /// this, since it is reattached instead. A session the executor no longer holds (one a pause or
    /// an answer already stopped) is passed over. What the stop reports the session spent is
    /// recorded against the step, its agent, and that session, so the budget check before the new
    /// launch counts it; a record the flow run refuses is logged. Returns the flow run as that record left
    /// it, or null when the stop failed and the flow run was paused instead, so no second agent runs
    /// beside one that would not stop.
    /// </summary>
    private async Task<FlowRun?> StopLeftSessionAsync(
        Principal principal, string project, FlowRun flowRun, FlowStep step, CancellationToken cancellationToken)
    {
        if (flowRun.Current is not { Kind: StepKind.Judgment, SessionId: Guid held } entry
            || !string.Equals(entry.Step, step.Name, StringComparison.Ordinal))
        {
            return flowRun;
        }

        var spend = new JudgmentSpend();
        try
        {
            Reattachment? found = await executor.ReattachAsync(new SessionHandle(held, entry.Performer), access: null, cancellationToken);
            if (found is ReattachedSession live)
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(Judgment.StopGrace);
                spend.Add(await executor.StopAsync(live.Handle, grace.Token));
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            await PauseAsync(
                principal, project, flowRun.Id,
                $"Step '{step.Name}' could not stop session {Ids.Short(held)} that agent '{entry.Performer}' was left running in, so no other was launched: {failure.Message}",
                FlowRunLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }

        if (spend.Reported is not { } usage)
        {
            return flowRun;
        }

        try
        {
            return await flowRuns.RecordUsageAsync(principal, project, flowRun.Id, entry.Step, entry.Performer, held, usage, cancellationToken);
        }
        catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(refused, "Flow run {FlowRun} could not record what the judgment of step '{Step}' spent.", Ids.Short(flowRun.Id), entry.Step);
            return flowRun;
        }
    }

    /// <summary>
    /// Stops the session the start-up sweep left running for a flow run that a person paused or stopped
    /// before the flow run's first advance read it, so no reattach's watch was there for that pause or
    /// stop to end, and records what the session reported spending. A session the executor no
    /// longer holds is not stopped, though usage it kept from before its retry is still recorded.
    /// </summary>
    private async Task EndClaimedSessionAsync(Principal principal, string project, FlowRun flowRun, CancellationToken cancellationToken)
    {
        if (flowRun.Current is not { AwaitsLaunchedSession: true, SessionId: Guid id } entry)
        {
            return;
        }

        // The pause or stop ended this launch, so its tokens go, as they would at its judgment's end.
        if (entry.LaunchId is Guid launch)
        {
            await EndSessionTokensAsync(launch, cancellationToken);
        }

        Reattachment? found = await executor.ReattachAsync(new SessionHandle(id, entry.Performer), access: null, cancellationToken);
        var spend = new JudgmentSpend();
        switch (found)
        {
            case ReattachedSession { Attempt: > 1 } retrying:
                spend.Add(retrying.Earlier);
                break;
            case LostSession lost:
                spend.Add(lost.Earlier);
                break;
        }

        if (found is ReattachedSession live)
        {
            logger.LogInformation(
                "Stopping session {Session} of agent '{Agent}', whose flow run {FlowRun} a person paused or stopped before it was reattached.",
                Ids.Short(id), entry.Performer, Ids.Short(flowRun.Id));
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(Judgment.StopGrace);
            spend.Add(await executor.StopAsync(live.Handle, grace.Token));
        }

        if (spend.Reported is not { } usage)
        {
            return;
        }

        try
        {
            await flowRuns.RecordUsageAsync(principal, project, flowRun.Id, entry.Step, entry.Performer, id, usage, cancellationToken);
        }
        catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(refused, "Flow run {FlowRun} could not record what the judgment of step '{Step}' spent.", Ids.Short(flowRun.Id), entry.Step);
        }
    }

    /// <summary>What a step recorded, with the flow run as that recording left it.</summary>
    private readonly record struct Recording(FlowRun FlowRun, JsonElement Value);

    /// <summary>
    /// Runs the step's judgment and records its answer, or pauses; null when the flow run paused. When
    /// <paramref name="spawnsAgain"/>, a resume performs again a visit that launched and paused
    /// without an answer, so one more spawn is counted on that visit once the budget checks have
    /// passed. The launch itself is recorded just before the agent runs, with the agent's hash and
    /// model spec, so a pause after the count and before the launch (an agent that does not resolve,
    /// a credential profile the project names that the node does not hold, a parcel that does not
    /// load) leaves that spawn unused, and the next resume uses it rather than counting another. The
    /// session the spawn returns is recorded before its first collect.
    /// </summary>
    private async Task<Recording?> JudgeAsync(
        Principal principal, string project, FlowRun flowRun, Flow flow, FlowStep step, bool spawnsAgain, CancellationToken cancellationToken)
    {
        if (await StopLeftSessionAsync(principal, project, flowRun, step, cancellationToken) is not { } stopped)
        {
            return null;
        }

        flowRun = stopped;

        Budget budget = await BudgetAsync(principal, project, flowRun, flow, cancellationToken);
        if (budget.Spent is { } spent)
        {
            await PauseAsync(principal, project, flowRun.Id, spent, FlowRunLevers.BudgetReached(), cancellationToken);
            return null;
        }

        if (spawnsAgain)
        {
            flowRun = await flowRuns.RespawnAsync(principal, project, flowRun.Id, step.Name, cancellationToken);
        }

        string agentName = step.Agent ?? string.Empty;
        Definition? published = await definitions.ResolveAgentAsync(principal, project, agentName, cancellationToken);
        if (published is null)
        {
            await PauseAsync(
                principal, project, flowRun.Id,
                $"Step '{step.Name}' names the agent '{agentName}', which resolution does not return as a published agent for project '{project}'.",
                FlowRunLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }

        AgentDefinition agent = AgentDefinition.FromJson(published.Content);
        string? named = await projects.ProfileOfAsync(principal, project, cancellationToken);
        CredentialProfile? profile = named is null
            ? null
            : profiles.Find(agent.Model.Vendor, named);
        if (named is not null && profile is null)
        {
            await PauseAsync(
                principal, project, flowRun.Id,
                $"Project '{project}' names the credential profile '{named}', which the node configuration loaded at start holds no entry for under the vendor '{agent.Model.Vendor}' of agent '{agent.Name}'.",
                FlowRunLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }

        Parcel parcel = await parcels.ShowAsync(principal, project, flowRun.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(flowRun.ParcelId)} in project '{project}'.");
        ParcelType type = await parcels.TypeVersionAsync(principal, project, parcel.TypeHash, cancellationToken)
            ?? throw new DomainException($"Parcel {Ids.Short(parcel.Id)} names a version of type '{parcel.TypeName}' that project '{project}' does not hold.");
        string context = ContextAssembler.Assemble(parcel, step, flowRun.Steps);
        string workingDirectory = home.FlowRunWorkPath(project, flowRun.Id);
        try
        {
            SessionFiles.WriteInputs(workingDirectory, parcel, type, context);
        }
        catch (Exception unwritable) when (unwritable is IOException or UnauthorizedAccessException)
        {
            await PauseAsync(
                principal, project, flowRun.Id,
                $"Step '{step.Name}' could not write its session's files into {workingDirectory}: {unwritable.Message}",
                FlowRunLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }

        var request = new JudgmentRequest(
            agent,
            context,
            workingDirectory,
            new SpawnCaps(budget.Ceiling, judgmentLimit),
            step.AnswerSchema ?? string.Empty,
            profile);

        // Watched before the launch is recorded, so a person's pause or stop saved after that record
        // ends the judgment even before its spawn returns, and one saved before it refuses the record.
        using var ended = new CancellationTokenSource();
        Watch(flowRun.Id, ended);
        Guid? launch = null;
        try
        {
            FlowRun spawned = await flowRuns.SpawnedAsync(principal, project, flowRun.Id, step.Name, published.ContentHash, agent.Model, cancellationToken);
            launch = spawned.Current?.LaunchId
                ?? throw new InvalidOperationException($"The launch of step '{step.Name}' on flow run {Ids.Short(flowRun.Id)} was recorded without a launch id.");
            Launching(launch.Value, here: true);

            // Issued once the launch is recorded and before the agent starts, since the session's
            // environment is fixed when its process starts; it resolves only while this launch runs.
            string token = await sessionTokens.IssueAsync(new SessionBinding(project.Trim(), flowRun.Id, launch.Value), cancellationToken);
            request = request with { Access = new SessionAccess(token, home.Path, project.Trim()) };
            var launched = new Launched(step.Name, agentName, published.ContentHash, agent.Model, budget.Ceiling);

            // The session's id is recorded before its first collect, so a daemon stop after that
            // leaves the session alive (the judgment does not stop it) and the flow run names it; the
            // next start reattaches it rather than launching again.
            Recording? recorded = await RunJudgmentAsync(
                principal, project, flowRun.Id, step, launched, judgmentLimit,
                (spend, bounded) => Judgment.RunAsync(
                    executor,
                    request,
                    async (session, opening) =>
                    {
                        launched.Session = session;
                        await flowRuns.SessionOpenedAsync(principal, project, flowRun.Id, step.Name, session.Id, opening);
                        launched.Recorded = true;
                    },
                    spend,
                    cancellationToken,
                    bounded),
                ended.Token,
                cancellationToken);

            // Recorded as answered or paused; a failure before either keeps the launch remembered.
            Launching(launch.Value, here: false);
            return recorded;
        }
        finally
        {
            if (launch is Guid issued)
            {
                await EndSessionTokensAsync(issued, cancellationToken);
            }

            Unwatch(flowRun.Id, ended);
        }
    }

    /// <summary>
    /// Performs a judgment visit a daemon restart found launched, unanswered, and not resumed since:
    /// its agent is not launched again on the spawn it already used. When the executor still holds
    /// the session the visit names, it is reattached, and its answer is collected, validated against
    /// the step's answer schema, retried once on the same session if it is invalid, and recorded
    /// with the agent hash and model spec the launch recorded, however the agent was published
    /// since. A session the restart found already on its retry is not retried again, and its answer
    /// is recorded with what the whole session spent. Its wall-clock limit runs from the launch, though a
    /// session that finished while no daemon ran is still collected once the limit is past, and a
    /// person's pause or stop saved before the reattach was watched ends it before its first collect.
    /// Nothing is recorded for the reattach itself. The process that ran across the restart keeps the
    /// token an earlier daemon issued, which resolves again because its binding is stored; the
    /// reattach also hands the session a token this daemon issues for the recorded launch, so a
    /// retry's process reaches this daemon as the launch's own principal. Both are removed once the
    /// judgment ends, unless a daemon stop ended it.
    /// When the visit names no session, or the executor no longer holds it, the flow run pauses instead,
    /// and its resume performs the step again with one more spawn counted. A session lost on its
    /// retry pauses carrying what its turns before the retry spent, which nothing recorded at the
    /// daemon stop. A session the visit names
    /// that could not be reattached for another reason (its launch recorded no agent hash and model
    /// spec, or the reattach failed) is stopped before that pause, so it does not run on unobserved.
    /// Null when it recorded no answer.
    /// </summary>
    private async Task<Recording?> ReattachAsync(
        Principal principal, string project, FlowRun flowRun, FlowStep step, FlowRunStepEntry current, CancellationToken cancellationToken)
    {
        Guid? launch = current.LaunchId;

        // A launch this runner made reaches here only after its judgment failed to record its answer
        // or its pause, with no daemon restart since. It is forgotten here, and remembered again
        // should this reattach fail the same way.
        bool restarted;
        lock (gate)
        {
            restarted = launch is not Guid made || !launchedHere.Remove(made);
        }

        string lostWhen = restarted ? "across a daemon restart" : "before its answer was recorded";
        string unobserved = restarted ? "observed" : "recorded";

        try
        {
            // Issued for the recorded launch, so it resolves only while the flow run still expects that
            // launch; the token the launch was spawned with is still stored and resolves beside it.
            SessionAccess? access = launch is Guid issuedFor
                ? new SessionAccess(
                    await sessionTokens.IssueAsync(new SessionBinding(project.Trim(), flowRun.Id, issuedFor), cancellationToken), home.Path, project.Trim())
                : null;
            Reattachment? found = null;
            bool settled = false;
            try
            {
                if (current is { SessionId: Guid id, AgentHash: not null, Model: not null })
                {
                    found = await executor.ReattachAsync(new SessionHandle(id, current.Performer), access, cancellationToken);
                    settled = true;
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                logger.LogWarning(failure, "Reattaching the session of step '{Step}' on flow run {FlowRun} failed.", step.Name, Ids.Short(flowRun.Id));
            }

            if (found is LostSession lostOnRetry && current is { AgentHash: string lostHash, Model: ModelSpec lostModel })
            {
                var lostLaunch = new Launched(step.Name, current.Performer, lostHash, lostModel, ceiling: null) { Session = lostOnRetry.Handle, Recorded = true };
                await PauseJudgmentAsync(
                    principal, project, flowRun.Id, lostLaunch,
                    $"Step '{step.Name}' lost the session agent '{current.Performer}' was launched in across a daemon restart (session {Ids.Short(lostOnRetry.Handle.Id)}, which ended on its retry without an answer), so the usage of its retry was not observed; the usage of its turns before the retry is recorded.",
                    FlowRunLevers.PerformAgain(step.Name),
                    lostOnRetry.Earlier,
                    cancellationToken);
                return null;
            }

            if (found is not ReattachedSession live
                || current is not { SpawnedAt: DateTimeOffset spawnedAt, AgentHash: string agentHash, Model: ModelSpec model })
            {
                // A session the executor did not say it no longer holds may still be running.
                if (!settled && current.SessionId is Guid left)
                {
                    await StopSessionAsync(new SessionHandle(left, current.Performer));
                }

                string which = current switch
                {
                    { SessionId: Guid held, AgentHash: not null, Model: not null } => settled
                        ? $"session {Ids.Short(held)}, which the executor no longer holds"
                        : $"session {Ids.Short(held)}, which could not be reattached",
                    { SessionId: Guid held } => $"session {Ids.Short(held)}, whose launch recorded no agent hash and model spec to record its answer with",
                    _ => "a session the daemon stopped before it was recorded",
                };
                await PauseAsync(
                    principal, project, flowRun.Id,
                    $"Step '{step.Name}' lost the session agent '{current.Performer}' was launched in {lostWhen} ({which}), so its usage was not {unobserved}.",
                    FlowRunLevers.PerformAgain(step.Name),
                    cancellationToken);
                return null;
            }

            var launched = new Launched(step.Name, current.Performer, agentHash, model, ceiling: null) { Session = live.Handle, Recorded = true };
            string schema = step.AnswerSchema ?? string.Empty;
            using var ended = new CancellationTokenSource();
            Watch(flowRun.Id, ended);

            // A session that finished while no daemon ran is collected however long ago it launched;
            // only the wall-clock limit is left out of what bounds that collect.
            using var finishedCollect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ended.Token);
            try
            {
                // A launch has a write after the watch that a pause refuses; a reattach has none, so a
                // pause or stop saved between this advance's read and the watch is read for here.
                FlowRun? now = await flowRuns.ShowAsync(principal, project, flowRun.Id, cancellationToken);
                if (now is not { Status: FlowRunStatus.Running, Current: { AwaitsLaunchedSession: true, SessionId: Guid awaited } } || awaited != live.Handle.Id)
                {
                    await ended.CancelAsync();
                }

                return await RunJudgmentAsync(
                    principal, project, flowRun.Id, step, launched, judgmentLimit - (clock.GetUtcNow() - spawnedAt),
                    (spend, bounded) => Judgment.ReattachAsync(executor, live, schema, spend, cancellationToken, finishedCollect.Token, bounded),
                    ended.Token,
                    cancellationToken);
            }
            finally
            {
                Unwatch(flowRun.Id, ended);
            }
        }
        catch (Exception) when (!restarted && launch is Guid kept && !cancellationToken.IsCancellationRequested)
        {
            Launching(kept, here: true);
            throw;
        }
        finally
        {
            if (launch is Guid issued)
            {
                await EndSessionTokensAsync(issued, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Runs the launched judgment through <paramref name="judge"/>, bounded by the daemon stop, the
    /// wall-clock limit, of which <paramref name="limitLeft"/> is left, and <paramref name="ended"/>,
    /// which a person's pause or stop cancels, and records its answer, or pauses; null when it
    /// recorded no answer. What the judgment spent is recorded however it ends, except at a daemon stop.
    /// </summary>
    private async Task<Recording?> RunJudgmentAsync(
        Principal principal,
        string project,
        Guid flowRunId,
        FlowStep step,
        Launched launched,
        TimeSpan limitLeft,
        Func<JudgmentSpend, CancellationToken, Task<JudgmentOutcome>> judge,
        CancellationToken ended,
        CancellationToken cancellationToken)
    {
        using var limit = new CancellationTokenSource(limitLeft > TimeSpan.Zero ? limitLeft : Timeout.InfiniteTimeSpan, clock);
        if (limitLeft <= TimeSpan.Zero)
        {
            await limit.CancelAsync();
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token, ended);
        var spend = new JudgmentSpend();
        JudgmentOutcome outcome;
        try
        {
            outcome = await judge(spend, bounded.Token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (ended.IsCancellationRequested)
        {
            // A person paused or stopped the flow run, so the judgment stopped its session; nothing is
            // recorded on the visit, but what the session spent is.
            logger.LogInformation(
                "The judgment of step '{Step}' on flow run {FlowRun} ended because a person paused or stopped the flow run: {Failure}",
                step.Name, Ids.Short(flowRunId), failure.Message);
            await KeepUsageAsync(principal, project, flowRunId, launched, spend.Reported, cancellationToken);
            return null;
        }
        catch (Exception failure) when (limit.IsCancellationRequested)
        {
            // The judgment wraps the cancellation when stopping the session failed too; the limit is still the cause.
            string stopped = failure switch
            {
                OperationCanceledException => "so its session was stopped",
                AggregateException => $"and stopping its session failed: {failure.Message}",
                _ => $"and it failed as it was cancelled: {failure.Message}",
            };
            await PauseJudgmentAsync(
                principal, project, flowRunId, launched,
                $"Step '{step.Name}' ran past the wall-clock limit of {Durations.Describe(judgmentLimit)} for one judgment, {stopped}.{CostUnreported(spend, launched.Ceiling)}",
                FlowRunLevers.PerformAgain(step.Name),
                spend.Reported,
                cancellationToken);
            return null;
        }
        catch (Exception failure)
        {
            await PauseJudgmentAsync(
                principal, project, flowRunId, launched,
                $"Step '{step.Name}' got no judgment from agent '{launched.Agent}': {failure.Message}{CostUnreported(spend, launched.Ceiling)}",
                FlowRunLevers.PerformAgain(step.Name),
                spend.Reported,
                cancellationToken);
            return null;
        }

        try
        {
            switch (outcome)
            {
                case JudgmentOutcome.Answered answered:
                    JsonElement answer = Parse(answered.Answer);
                    FlowRun recorded;
                    try
                    {
                        recorded = await flowRuns.RecordAnswerAsync(
                            principal, project, flowRunId, step.Name, launched.Agent, answered.Session.Id, launched.AgentHash, launched.Model, answer, answered.Usage, cancellationToken);
                    }
                    catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
                    {
                        await SettleRefusedAsync(principal, project, flowRunId, launched, answered.Usage, refused, cancellationToken);
                        return null;
                    }

                    return new Recording(recorded, answer);
                case JudgmentOutcome.Paused paused:
                    await PauseJudgmentAsync(principal, project, flowRunId, launched, paused.Reason, paused.Lever, paused.Usage, cancellationToken);
                    return null;
                default:
                    throw new InvalidOperationException($"A judgment ended in an outcome the runner does not know: {outcome.GetType().Name}.");
            }
        }
        finally
        {
            await StopSessionAsync(outcome.Session);
        }
    }

    /// <summary>
    /// What a pause reason adds when the session reported usage for its attempt in progress without
    /// its cost: that no cost is recorded for that attempt, and, when this daemon launched it, the
    /// cost ceiling it ran under, which bounds what it may have spent. Nothing when every reported
    /// attempt carried its cost.
    /// </summary>
    private static string CostUnreported(JudgmentSpend spend, decimal? ceiling) => (spend.CostUnreported, ceiling) switch
    {
        (false, _) => string.Empty,
        (true, decimal known) => $" The session did not report the cost of its attempt in progress, so none is recorded for it; the spawn ran under a cost ceiling of {Dollars(known)}.",
        (true, null) => " The session did not report the cost of its attempt in progress, so none is recorded for it.",
    };

    /// <summary>
    /// The judgment a flow run's step launched: its step and agent, the agent hash and model spec its
    /// answer is recorded with, the cost ceiling its spawn carried when this daemon launched it, the
    /// session its spawn returned once it has, and whether the flow run recorded that session, which is
    /// when the visit expects it.
    /// </summary>
    private sealed class Launched(string step, string agent, string agentHash, ModelSpec model, decimal? ceiling)
    {
        public string Step { get; } = step;

        public string Agent { get; } = agent;

        public string AgentHash { get; } = agentHash;

        public ModelSpec Model { get; } = model;

        public decimal? Ceiling { get; } = ceiling;

        public SessionHandle? Session { get; set; }

        public bool Recorded { get; set; }
    }

    /// <summary>
    /// Pauses the flow run for a judgment that gave no answer, recording the <paramref name="usage"/> it
    /// reported. A judgment whose session the flow run recorded pauses only while the visit still expects
    /// that session; when the pause is refused, what it spent is settled as a refused answer's is.
    /// </summary>
    private async Task PauseJudgmentAsync(
        Principal principal,
        string project,
        Guid flowRunId,
        Launched launched,
        string reason,
        string lever,
        Measurement? usage,
        CancellationToken cancellationToken)
    {
        Guid? session = launched is { Recorded: true, Session: { } held } ? held.Id : null;
        try
        {
            await flowRuns.PauseAsync(principal, project, flowRunId, reason, lever, usage, session, cancellationToken);
        }
        catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
        {
            await SettleRefusedAsync(principal, project, flowRunId, launched, usage, refused, cancellationToken);
        }
    }

    /// <summary>
    /// A judgment whose answer or pause the flow run's rules refused. When the flow run is no longer running
    /// (a person paused or stopped it while the agent worked), or its visit no longer expects the
    /// session (a resume came after the launch, and its own pass performs the step again), only what
    /// the judgment spent is recorded. Otherwise the flow run pauses quoting the rule, carrying that
    /// usage, so a running flow run is never left with no worker and no pause.
    /// </summary>
    private async Task SettleRefusedAsync(
        Principal principal,
        string project,
        Guid flowRunId,
        Launched launched,
        Measurement? usage,
        DomainException refused,
        CancellationToken cancellationToken)
    {
        FlowRun? now = await flowRuns.ShowAsync(principal, project, flowRunId, cancellationToken);
        if (now is { Status: FlowRunStatus.Running } && !NoLongerExpected(now, launched))
        {
            try
            {
                await flowRuns.PauseAsync(
                    principal, project, flowRunId,
                    $"Flow run could not go on from step '{now.CurrentStep}': {refused.Message}",
                    FlowRunLevers.PerformAgain(now.CurrentStep),
                    usage,
                    session: null,
                    cancellationToken);
                return;
            }
            catch (DomainException pauseRefused)
            {
                logger.LogError(pauseRefused, "Flow run {FlowRun} could neither record its judgment nor pause: {Rule}", Ids.Short(flowRunId), refused.Message);
            }
        }
        else
        {
            logger.LogInformation("The judgment of step '{Step}' on flow run {FlowRun} records only what it spent: {Rule}", launched.Step, Ids.Short(flowRunId), refused.Message);
        }

        await KeepUsageAsync(principal, project, flowRunId, launched, usage, cancellationToken);
    }

    /// <summary>Whether the flow run's current visit to the launched step has moved past the launched session.</summary>
    private static bool NoLongerExpected(FlowRun flowRun, Launched launched) =>
        launched.Session is { } session
        && flowRun.Current is { Kind: StepKind.Judgment } current
        && string.Equals(current.Step, launched.Step, StringComparison.Ordinal)
        && (current.ResumedAt is not null || current.SessionId is Guid held && held != session.Id);

    /// <summary>
    /// Records what a judgment spent when neither its answer nor its pause was recorded. There is
    /// nothing to record when the judgment reported nothing; a refusal is logged, since the flow run is
    /// as it should be and only the usage is lost.
    /// </summary>
    private async Task KeepUsageAsync(
        Principal principal, string project, Guid flowRunId, Launched launched, Measurement? usage, CancellationToken cancellationToken)
    {
        if (usage is null || launched.Session is not { } session)
        {
            return;
        }

        try
        {
            await flowRuns.RecordUsageAsync(principal, project, flowRunId, launched.Step, launched.Agent, session.Id, usage, cancellationToken);
        }
        catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(refused, "Flow run {FlowRun} could not record what the judgment of step '{Step}' spent.", Ids.Short(flowRunId), launched.Step);
        }
    }

    /// <summary>
    /// Remembers a launch this runner made while its judgment may yet fail before recording its
    /// answer or its pause, or forgets it once one of them is recorded.
    /// </summary>
    private void Launching(Guid launch, bool here)
    {
        lock (gate)
        {
            _ = here ? launchedHere.Add(launch) : launchedHere.Remove(launch);
        }
    }

    /// <summary>Lets a person's pause or stop of the flow run end the judgment <paramref name="ended"/> bounds.</summary>
    private void Watch(Guid flowRunId, CancellationTokenSource ended)
    {
        lock (gate)
        {
            if (workers.TryGetValue(flowRunId, out Worker? worker))
            {
                worker.Judging = ended;
            }
        }
    }

    private void Unwatch(Guid flowRunId, CancellationTokenSource ended)
    {
        lock (gate)
        {
            if (workers.TryGetValue(flowRunId, out Worker? worker) && ReferenceEquals(worker.Judging, ended))
            {
                worker.Judging = null;
            }
        }
    }

    /// <summary>Lets a person's stop of the flow run cancel the action <paramref name="stopped"/> bounds, at once when the stop is already saved.</summary>
    private void WatchAction(Guid flowRunId, CancellationTokenSource stopped)
    {
        lock (gate)
        {
            if (workers.TryGetValue(flowRunId, out Worker? worker))
            {
                worker.Acting = stopped;
                if (worker.Stopping)
                {
                    stopped.Cancel();
                }
            }
        }
    }

    private void UnwatchAction(Guid flowRunId, CancellationTokenSource stopped)
    {
        lock (gate)
        {
            if (workers.TryGetValue(flowRunId, out Worker? worker) && ReferenceEquals(worker.Acting, stopped))
            {
                worker.Acting = null;
            }
        }
    }

    /// <summary>
    /// Removes the flow run's worktree once the flow run has ended or been stopped and its worker is between
    /// steps. A flow run that never made one is left alone; a removal that fails is logged, since the end
    /// or the stop is already recorded, and the next start's sweep tries again.
    /// </summary>
    private async Task RemoveWorktreeAsync(string project, Guid flowRunId, CancellationToken cancellationToken)
    {
        try
        {
            if (await clones.RemoveAsync(project, flowRunId, cancellationToken))
            {
                logger.LogInformation("Removed the worktree of flow run {FlowRun} in project {Project}.", Ids.Short(flowRunId), project);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            logger.LogError(failure, "Removing the worktree of flow run {FlowRun} in project {Project} failed; the daemon's next start sweeps it.", Ids.Short(flowRunId), project);
        }
    }

    /// <summary>
    /// What is left of the budgets a judgment is performed under. <see cref="Spent"/> is the reason a
    /// judgment may not be performed because a budget is spent, or null when none is;
    /// <see cref="Ceiling"/> is the cost ceiling its spawn carries otherwise.
    /// </summary>
    private readonly record struct Budget(string? Spent, decimal Ceiling);

    /// <summary>
    /// The budgets a judgment is performed under: the flow run's own recorded cost against its flow's
    /// budget, then, for a flow run with a parent, the recorded cost of every flow run of its chain against
    /// the budget of the flow its root flow run follows. Either reached is the reason no judgment is
    /// performed; otherwise the ceiling is the smaller of the two remainders. The chain is summed by
    /// reading its flow runs, which all run on this flow run's parcel.
    /// </summary>
    private async Task<Budget> BudgetAsync(
        Principal principal, string project, FlowRun flowRun, Flow flow, CancellationToken cancellationToken)
    {
        if (flowRun.Cost >= flow.Budget)
        {
            return new Budget(
                $"Flow run {Ids.Short(flowRun.Id)} has recorded a cost of {Dollars(flowRun.Cost)}, which reaches the budget of {Dollars(flow.Budget)} of flow '{flow.Name}'; a flow run performs no judgment once its recorded cost reaches its flow's budget.",
                0m);
        }

        decimal flowRunRemainder = flow.Budget - flowRun.Cost;

        // A flow run with no parent is its own chain, already held to its own flow's budget above.
        if (flowRun.ParentFlowRunId is null)
        {
            return new Budget(null, flowRunRemainder);
        }

        IReadOnlyList<FlowRun> chain = await flowRuns.ChainOfAsync(principal, project, flowRun, cancellationToken);
        FlowRun root = chain.FirstOrDefault(f => f.Id == flowRun.RootFlowRunId)
            ?? throw new DomainException($"Flow run {Ids.Short(flowRun.Id)} names flow run {Ids.Short(flowRun.RootFlowRunId)} as the root of its chain, which its parcel does not hold.");
        Flow rootFlow = await flowRuns.FlowOfAsync(principal, project, root, cancellationToken);
        decimal chainCost = chain.Sum(f => f.Cost);
        return chainCost >= rootFlow.Budget
            ? new Budget(
                $"The chain of flow run {Ids.Short(flowRun.Id)}, rooted at flow run {Ids.Short(root.Id)}, has recorded a cost of {Dollars(chainCost)} across its flow runs, which reaches the budget of {Dollars(rootFlow.Budget)} of flow '{rootFlow.Name}' that its root flow run follows; a chain performs no judgment once its recorded cost reaches that budget.",
                0m)
            : new Budget(null, Math.Min(flowRunRemainder, rootFlow.Budget - chainCost));
    }

    private static string Dollars(decimal amount) => string.Create(CultureInfo.InvariantCulture, $"${amount:0.00##}");

    /// <summary>Performs the step's action and records its result, or pauses; null when the flow run paused.</summary>
    private async Task<Recording?> ActAsync(
        Principal principal, string project, FlowRun flowRun, Flow flow, FlowStep step, bool resumed, CancellationToken cancellationToken)
    {
        var request = new ActionRequest(principal, project, flowRun.Id, flowRun.ParcelId, flow, step, resumed);
        ActionOutcome outcome;

        // A person's stop, saved before the action starts or while it runs, cancels it.
        using var stopped = new CancellationTokenSource();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopped.Token);
        WatchAction(flowRun.Id, stopped);
        try
        {
            outcome = await actions.PerformAsync(request, bounded.Token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (stopped.IsCancellationRequested)
        {
            logger.LogInformation(
                "The action '{Action}' of step '{Step}' on flow run {FlowRun} ended because a person stopped the flow run; it records nothing: {Failure}",
                step.Action, step.Name, Ids.Short(flowRun.Id), failure.Message);
            return null;
        }
        catch (Exception failure)
        {
            outcome = new ActionOutcome.Paused(
                $"Step '{step.Name}' could not perform the action '{step.Action}': {failure.Message}",
                FlowRunLevers.PerformAgain(step.Name));
        }
        finally
        {
            UnwatchAction(flowRun.Id, stopped);
        }

        if (stopped.IsCancellationRequested)
        {
            // Stopped as the action finished: what it found is not recorded on a stopped flow run.
            return null;
        }

        switch (outcome)
        {
            case ActionOutcome.Done done:
                FlowRun recorded = await flowRuns.RecordResultAsync(principal, project, flowRun.Id, step.Name, step.Action ?? string.Empty, done.Result, cancellationToken);
                return new Recording(recorded, done.Result);
            case ActionOutcome.Paused paused:
                await PauseAsync(principal, project, flowRun.Id, paused.Reason, paused.Lever, cancellationToken);
                return null;
            default:
                throw new InvalidOperationException($"An action ended in an outcome the runner does not know: {outcome.GetType().Name}.");
        }
    }

    /// <summary>Applies the interpreter's next move for the recorded answer or result; true when the flow run entered another step.</summary>
    private async Task<bool> RouteAsync(
        Principal principal, string project, FlowRun flowRun, Flow flow, FlowStep step, JsonElement recorded, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonSerializer.SerializeToDocument(recorded);
        FlowRunMove move = Interpreter.Decide(flow, flowRun.Position, document);
        switch (move)
        {
            case FlowRunMove.GoTo goTo:
                await flowRuns.StartStepAsync(principal, project, flowRun.Id, goTo.Step, cancellationToken);
                return true;
            case FlowRunMove.End end:
                await flowRuns.EndAsync(principal, project, flowRun.Id, end.Outcome, cancellationToken);
                await RemoveWorktreeAsync(project, flowRun.Id, cancellationToken);
                return false;
            case FlowRunMove.Pause pause:
                await PauseAsync(principal, project, flowRun.Id, pause.Reason, FlowRunLevers.PerformAgain(step.Name), cancellationToken);
                return false;
            default:
                throw new InvalidOperationException($"The interpreter returned a move the runner does not know: {move.GetType().Name}.");
        }
    }

    private Task PauseAsync(Principal principal, string project, Guid flowRunId, string reason, string lever, CancellationToken cancellationToken) =>
        flowRuns.PauseAsync(principal, project, flowRunId, reason, lever, cancellationToken);

    /// <summary>
    /// A recording the flow run's rules refused. When the flow run is no longer running (stopped while
    /// its step was performed, say) nothing more is recorded; otherwise it pauses quoting the rule,
    /// so a running flow run is never left with no worker and no pause.
    /// </summary>
    private async Task PauseOnRefusalAsync(Principal principal, string project, FlowRun flowRun, DomainException refused, CancellationToken cancellationToken)
    {
        FlowRun? now = await flowRuns.ShowAsync(principal, project, flowRun.Id, cancellationToken);
        if (now is not { Status: FlowRunStatus.Running })
        {
            logger.LogInformation("Flow run {FlowRun} is no longer running, so its step records nothing: {Rule}", Ids.Short(flowRun.Id), refused.Message);
            return;
        }

        try
        {
            await PauseAsync(
                principal, project, flowRun.Id,
                $"Flow run could not go on from step '{now.CurrentStep}': {refused.Message}",
                FlowRunLevers.PerformAgain(now.CurrentStep),
                cancellationToken);
        }
        catch (DomainException pauseRefused)
        {
            logger.LogError(pauseRefused, "Flow run {FlowRun} could neither record its step nor pause: {Rule}", Ids.Short(flowRun.Id), refused.Message);
        }
    }

    /// <summary>
    /// Ends a judgment's session once its outcome is in hand, since a resume performs the step
    /// again with a new spawn. Like the judgment's own stop after a failure, it takes a bounded
    /// token of its own rather than the worker's, so a daemon stop does not leave the session open.
    /// </summary>
    private async Task StopSessionAsync(SessionHandle session)
    {
        try
        {
            using var bounded = new CancellationTokenSource(Judgment.StopGrace);
            await executor.StopAsync(session, bounded.Token);
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Stopping session {Session} for agent '{Agent}' failed.", session.Id, session.Agent);
        }
    }

    private static JsonElement Parse(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        return document.RootElement.Clone();
    }

    private sealed class Worker
    {
        /// <summary>Completes when this worker stops advancing the flow run.</summary>
        public Task Idle { get; set; } = Task.CompletedTask;

        /// <summary>Another request arrived while this worker ran, so it reads the flow run once more before it finishes.</summary>
        public bool Again { get; set; }

        /// <summary>Cancelled by a person's pause or stop to end the judgment this worker runs; null while it runs none.</summary>
        public CancellationTokenSource? Judging { get; set; }

        /// <summary>Cancelled by a person's stop to end the action this worker performs; null while it performs none.</summary>
        public CancellationTokenSource? Acting { get; set; }

        /// <summary>A person's stop of the flow run was saved, so an action this worker starts from now on is cancelled at once.</summary>
        public bool Stopping { get; set; }
    }
}
