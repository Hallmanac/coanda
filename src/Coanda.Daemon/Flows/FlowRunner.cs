using System.Globalization;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Fisher;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Flows;

/// <summary>
/// Advances live flows on the daemon's own workers: performs the current step through the
/// executor or an action, records its result on the flow, asks the interpreter for the next
/// move, and applies it, until the flow ends, pauses, or stops. At most one worker advances a
/// flow; a request for a flow already being advanced is folded into that worker, which reads
/// the flow once more before it finishes. Before any step, a flow deeper in its chain than its
/// contour's follow allows pauses; before a judgment, a flow whose recorded cost has reached its contour's
/// budget, or whose chain's recorded cost has reached the budget of the contour its root runs, pauses.
/// Both checks run on every pass, so a resume of such a pause pauses again rather than overriding it.
/// A resumed judgment step spawns another agent, so it pauses once the flow's spawns reach its
/// contour's spawn cap, whether the visit resumed had recorded an answer or paused without one.
/// A resume of a visit that recorded performs the step again as a new visit, so it pauses before
/// entering the step when every transition out of it has a visit cap below that visit; the
/// spawn-cap check runs first, so it answers when both bind.
/// </summary>
/// <remarks>
/// Every write is made as the flow's own principal, of the Agent role, through a flow service
/// of the runner's own: the service the daemon registers hands resumes to this runner, so the
/// runner cannot also take that one without a cycle. A daemon stop cancels the step in progress
/// and records no answer, result, or pause for it, leaving the flow live and unpaused so the next
/// start performs the step again. A judgment whose session the flow already names keeps that
/// session alive across the stop, for a reattach; one stopped before its session was recorded has
/// the session stopped. Until a reattach path exists, the next start stops a session left that way
/// before it launches the judgment again, so no two agents run in one flow's working directory. A step recorded before the stop but not yet routed is routed without
/// being performed again. A person's pause or stop, once saved, ends the judgment in progress at
/// once, its session stopped through the executor; an action in progress runs on. Every judgment
/// that launched a session records what it spent: in its answer, in its pause, or, when neither
/// was recorded (a person paused or stopped the flow, or its visit no longer expects the session),
/// in a record of its usage alone. An answer or a pause from a session the visit no longer expects,
/// because a resume came after its launch, does not pause the flow: the resume's own pass performs
/// the step again. Each launch, once recorded and before its agent starts, is issued a session token
/// of its own (<see cref="SessionTokens"/>), handed to the executor in the spawn's
/// <see cref="SessionAccess"/>, and forgotten when the judgment ends.
/// </remarks>
public sealed class FlowRunner : IFlowAdvancer, IHostedService, IDisposable
{
    /// <summary>The principal that reads the node's live flows when the daemon starts.</summary>
    public static readonly Principal ScanPrincipal = new("coanda-runner", Role.Agent);

    private readonly FlowService flows;
    private readonly ProjectService projects;
    private readonly ParcelService parcels;
    private readonly DefinitionService definitions;
    private readonly FlowActions actions;
    private readonly IAgentExecutor executor;
    private readonly CredentialProfiles profiles;
    private readonly NodeHome home;
    private readonly SessionTokens sessionTokens;
    private readonly TimeProvider clock;
    private readonly TimeSpan judgmentLimit;
    private readonly ILogger<FlowRunner> logger;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Worker> workers = [];
    private bool stopRequested;

    public FlowRunner(
        IDocumentStore store,
        ProjectService projects,
        ParcelService parcels,
        DefinitionService definitions,
        FlowActions actions,
        IAgentExecutor executor,
        CredentialProfiles profiles,
        NodeHome home,
        SessionTokens sessionTokens,
        TimeProvider clock,
        FlowRunnerOptions options,
        ILogger<FlowRunner> logger)
    {
        flows = new FlowService(store, clock);
        this.projects = projects;
        this.parcels = parcels;
        this.definitions = definitions;
        this.actions = actions;
        this.executor = executor;
        this.profiles = profiles;
        this.home = home;
        this.sessionTokens = sessionTokens;
        this.clock = clock;
        judgmentLimit = options.JudgmentLimit;
        this.logger = logger;
    }

    /// <summary>The principal every write made while advancing a flow carries: the Agent role, named for the flow.</summary>
    public static Principal PrincipalFor(Guid flowId) => new($"flow:{flowId}", Role.Agent);

    public Task RequestAdvance(string project, Guid flowId)
    {
        lock (gate)
        {
            if (stopRequested)
            {
                return Task.CompletedTask;
            }

            if (workers.TryGetValue(flowId, out Worker? running))
            {
                running.Again = true;
                return running.Idle;
            }

            var worker = new Worker();
            workers.Add(flowId, worker);
            worker.Idle = Task.Run(() => WorkAsync(project, flowId, worker, stopping.Token));
            return worker.Idle;
        }
    }

    public void EndJudgment(Guid flowId)
    {
        lock (gate)
        {
            // Only the state changes here; the judgment's own continuations run on the pool, not on
            // the caller's thread, and the source is disposed only after it is no longer watched.
            if (workers.TryGetValue(flowId, out Worker? running) && running.Judging is { } judging)
            {
                _ = judging.CancelAsync();
            }
        }
    }

    /// <summary>Requests an advance of every live flow on the node; a paused one is read and left as it is.</summary>
    public async Task AdvanceLiveAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Project, Guid FlowId)> live = await flows.ListLiveAsync(ScanPrincipal, cancellationToken);
        foreach ((string project, Guid flowId) in live)
        {
            _ = RequestAdvance(project, flowId);
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
            logger.LogWarning("The daemon stopped before {Count} flow workers finished giving up their steps.", running.Length);
        }
    }

    public void Dispose() => stopping.Dispose();

    private async Task WorkAsync(string project, Guid flowId, Worker worker, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await AdvanceAsync(project, flowId, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // A daemon stop: the step in progress records nothing and is performed again on the next start.
            }
            catch (Exception failure)
            {
                logger.LogError(failure, "Advancing flow {Flow} in project {Project} failed; it is left as it was recorded.", Ids.Short(flowId), project);
            }

            lock (gate)
            {
                if (!worker.Again || cancellationToken.IsCancellationRequested)
                {
                    workers.Remove(flowId);
                    return;
                }

                worker.Again = false;
            }
        }
    }

    private async Task AdvanceAsync(string project, Guid flowId, CancellationToken cancellationToken)
    {
        Principal principal = PrincipalFor(flowId);
        bool moving = true;
        while (moving)
        {
            Flow? flow = await flows.ShowAsync(principal, project, flowId, cancellationToken);
            if (flow is not { Status: FlowStatus.Running })
            {
                return;
            }

            try
            {
                moving = await StepAsync(principal, project, flow, cancellationToken);
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                await PauseOnRefusalAsync(principal, project, flow, refused, cancellationToken);
                return;
            }
        }
    }

    /// <summary>Takes the flow one step on; true when it entered another step and is still running.</summary>
    private async Task<bool> StepAsync(Principal principal, string project, Flow flow, CancellationToken cancellationToken)
    {
        Contour contour = await flows.ContourOfAsync(principal, project, flow, cancellationToken);
        FlowStepEntry current = flow.Current
            ?? throw new DomainException($"Flow {Ids.Short(flow.Id)} has entered no step, so there is nothing to perform.");
        ContourStep step = contour.Step(current.Step)
            ?? throw new DomainException($"Contour '{contour.Name}' has no step '{current.Step}', so flow {Ids.Short(flow.Id)} cannot perform it.");

        if (contour.Follow is { } follow && flow.Depth > follow.DepthCap)
        {
            await PauseAsync(
                principal, project, flow.Id,
                $"Flow {Ids.Short(flow.Id)} is at depth {flow.Depth} of its chain, past the depth cap of {follow.DepthCap} on the follow of contour '{contour.Name}'; a flow deeper than its follow's depth cap performs no step.",
                FlowLevers.DepthCapReached(contour.Name),
                cancellationToken);
            return false;
        }

        // A resumed judgment spawns another agent, whether its visit recorded or not, once its
        // counted spawn is used: not when the visit paused before it launched, and not when this
        // resume's spawn is already counted (a restart performing the resumed visit again). A
        // judgment the interpreter entered within the cap is performed once, so it is not checked.
        bool spawnsAgain = current.SpawnsAgainOnResume;
        if (spawnsAgain && flow.Spawns >= contour.SpawnCap)
        {
            await PauseAsync(
                principal, project, flow.Id,
                $"Contour '{contour.Name}' reached its spawn cap of {contour.SpawnCap}; performing judgment step '{step.Name}' again would spawn another agent.",
                FlowLevers.SpawnCapReached(contour.Name),
                cancellationToken);
            return false;
        }

        // A resume after this visit recorded (a pause the interpreter decided) performs the step
        // again as a new visit; a recording with no resume since is routed, never performed again.
        if (current.IsRecorded && current.ResumedAt is not null)
        {
            // A new visit no transition's visit cap allows would be performed only to pause on the
            // same cap, so it pauses before the step is entered, spawned, or acted.
            int visits = flow.Position.VisitsTo(step.Name);
            if (!Interpreter.CanFireAtNextVisit(step, visits))
            {
                await PauseAsync(
                    principal, project, flow.Id,
                    $"Step '{step.Name}' has a visit count of {visits}, and every transition out of it has a visit cap below {visits + 1}; performing it again could only pause on a visit cap.",
                    FlowLevers.VisitCapsSpent(step.Name),
                    cancellationToken);
                return false;
            }

            await flows.StartStepAsync(principal, project, flow.Id, step.Name, cancellationToken);
            return true;
        }

        Recording? recorded = current.IsRecorded
            ? (current.Answer ?? current.Result) is JsonElement held ? new Recording(flow, held) : null
            : step.IsJudgment
                ? await JudgeAsync(principal, project, flow, contour, step, spawnsAgain, cancellationToken)
                : await ActAsync(principal, project, flow, contour, step, current.ResumedAt is not null, cancellationToken);

        // Routed against the flow as the recording left it, so a spawn counted on this pass is in the position.
        return recorded is Recording recording
            && await RouteAsync(principal, project, recording.Flow, contour, step, recording.Value, cancellationToken);
    }

    /// <summary>
    /// Stops the session a daemon stop left alive on the visit about to be performed, before the
    /// judgment launches another in the same working directory. The flow names that session until
    /// the new launch is recorded, and nothing reattaches to it yet, so it is ended here rather than
    /// left running with nobody holding it. A session the executor no longer holds (one a pause or
    /// an answer already stopped) is passed over. Returns false when the stop failed and the flow
    /// was paused instead, so no second agent runs beside one that would not stop.
    /// </summary>
    private async Task<bool> StopLeftSessionAsync(
        Principal principal, string project, Flow flow, ContourStep step, CancellationToken cancellationToken)
    {
        if (flow.Current is not { Kind: StepKind.Judgment, SessionId: Guid held } entry
            || !string.Equals(entry.Step, step.Name, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            SessionHandle? live = await executor.ReattachAsync(new SessionHandle(held, entry.Performer), cancellationToken);
            if (live is not null)
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(Judgment.StopGrace);
                await executor.StopAsync(live, grace.Token);
            }

            return true;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            await PauseAsync(
                principal, project, flow.Id,
                $"Step '{step.Name}' could not stop session {Ids.Short(held)} that agent '{entry.Performer}' was left running in, so no other was launched: {failure.Message}",
                FlowLevers.PerformAgain(step.Name),
                cancellationToken);
            return false;
        }
    }

    /// <summary>What a step recorded, with the flow as that recording left it.</summary>
    private readonly record struct Recording(Flow Flow, JsonElement Value);

    /// <summary>
    /// Runs the step's judgment and records its answer, or pauses; null when the flow paused. When
    /// <paramref name="spawnsAgain"/>, a resume performs again a visit that launched and paused
    /// without an answer, so one more spawn is counted on that visit once the budget checks have
    /// passed. The launch itself is recorded just before the agent runs, with the agent's hash and
    /// model spec, so a pause after the count and before the launch (an agent that does not resolve,
    /// a credential profile the project names that the node does not hold, a parcel that does not
    /// load) leaves that spawn unused, and the next resume uses it rather than counting another. The
    /// session the spawn returns is recorded before its first collect.
    /// </summary>
    private async Task<Recording?> JudgeAsync(
        Principal principal, string project, Flow flow, Contour contour, ContourStep step, bool spawnsAgain, CancellationToken cancellationToken)
    {
        if (!await StopLeftSessionAsync(principal, project, flow, step, cancellationToken))
        {
            return null;
        }

        Budget budget = await BudgetAsync(principal, project, flow, contour, cancellationToken);
        if (budget.Spent is { } spent)
        {
            await PauseAsync(principal, project, flow.Id, spent, FlowLevers.BudgetReached(), cancellationToken);
            return null;
        }

        if (spawnsAgain)
        {
            flow = await flows.RespawnAsync(principal, project, flow.Id, step.Name, cancellationToken);
        }

        string agentName = step.Agent ?? string.Empty;
        Definition? published = await definitions.ResolveAgentAsync(principal, project, agentName, cancellationToken);
        if (published is null)
        {
            await PauseAsync(
                principal, project, flow.Id,
                $"Step '{step.Name}' names the agent '{agentName}', which resolution does not return as a published agent for project '{project}'.",
                FlowLevers.PerformAgain(step.Name),
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
                principal, project, flow.Id,
                $"Project '{project}' names the credential profile '{named}', which the node configuration loaded at start holds no entry for under the vendor '{agent.Model.Vendor}' of agent '{agent.Name}'.",
                FlowLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }

        Parcel parcel = await parcels.ShowAsync(principal, project, flow.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(flow.ParcelId)} in project '{project}'.");
        ParcelType type = await parcels.TypeVersionAsync(principal, project, parcel.TypeHash, cancellationToken)
            ?? throw new DomainException($"Parcel {Ids.Short(parcel.Id)} names a version of type '{parcel.TypeName}' that project '{project}' does not hold.");
        string context = ContextAssembler.Assemble(parcel, step);
        string workingDirectory = home.FlowWorkPath(project, flow.Id);
        try
        {
            SessionFiles.WriteInputs(workingDirectory, parcel, type, context);
        }
        catch (Exception unwritable) when (unwritable is IOException or UnauthorizedAccessException)
        {
            await PauseAsync(
                principal, project, flow.Id,
                $"Step '{step.Name}' could not write its session's files into {workingDirectory}: {unwritable.Message}",
                FlowLevers.PerformAgain(step.Name),
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
        Watch(flow.Id, ended);
        Guid? launch = null;
        try
        {
            Flow spawned = await flows.SpawnedAsync(principal, project, flow.Id, step.Name, published.ContentHash, agent.Model, cancellationToken);
            launch = spawned.Current?.LaunchId
                ?? throw new InvalidOperationException($"The launch of step '{step.Name}' on flow {Ids.Short(flow.Id)} was recorded without a launch id.");

            // Issued once the launch is recorded and before the agent starts, since the session's
            // environment is fixed when its process starts; it resolves only while this launch runs.
            string token = sessionTokens.Issue(new SessionBinding(project.Trim(), flow.Id, launch.Value));
            request = request with { Access = new SessionAccess(token, home.Path, project.Trim()) };
            return await RunJudgmentAsync(principal, project, flow.Id, step, agent, published.ContentHash, request, ended.Token, cancellationToken);
        }
        finally
        {
            if (launch is Guid issued)
            {
                sessionTokens.Revoke(issued);
            }

            Unwatch(flow.Id, ended);
        }
    }

    /// <summary>
    /// Runs the launched judgment, bounded by the daemon stop, the wall-clock limit, and
    /// <paramref name="ended"/>, which a person's pause or stop cancels, and records its answer, or
    /// pauses; null when it recorded no answer. What the judgment spent is recorded however it ends,
    /// except at a daemon stop.
    /// </summary>
    private async Task<Recording?> RunJudgmentAsync(
        Principal principal,
        string project,
        Guid flowId,
        ContourStep step,
        AgentDefinition agent,
        string agentHash,
        JudgmentRequest request,
        CancellationToken ended,
        CancellationToken cancellationToken)
    {
        string agentName = step.Agent ?? string.Empty;
        using var limit = new CancellationTokenSource(judgmentLimit, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token, ended);
        var spend = new JudgmentSpend();
        var launched = new Launched(step.Name, agentName);
        JudgmentOutcome outcome;
        try
        {
            // The session's id is recorded before its first collect, so a daemon stop after that
            // leaves the session alive (the judgment does not stop it) and the flow names it; the
            // next start stops it in StopLeftSessionAsync before launching again.
            outcome = await Judgment.RunAsync(
                executor,
                request,
                async (session, token) =>
                {
                    launched.Session = session;
                    await flows.SessionOpenedAsync(principal, project, flowId, step.Name, session.Id, token);
                    launched.Recorded = true;
                },
                spend,
                cancellationToken,
                bounded.Token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (ended.IsCancellationRequested)
        {
            // A person paused or stopped the flow, so the judgment stopped its session; nothing is
            // recorded on the visit, but what the session spent is.
            logger.LogInformation(
                "The judgment of step '{Step}' on flow {Flow} ended because a person paused or stopped the flow: {Failure}",
                step.Name, Ids.Short(flowId), failure.Message);
            await KeepUsageAsync(principal, project, flowId, launched, spend.Reported, cancellationToken);
            return null;
        }
        catch (Exception failure) when (limit.IsCancellationRequested)
        {
            // Judgment.RunAsync wraps the cancellation when stopping the session failed too; the limit is still the cause.
            string stopped = failure switch
            {
                OperationCanceledException => "so its session was stopped",
                AggregateException => $"and stopping its session failed: {failure.Message}",
                _ => $"and it failed as it was cancelled: {failure.Message}",
            };
            await PauseJudgmentAsync(
                principal, project, flowId, launched,
                $"Step '{step.Name}' ran past the wall-clock limit of {Describe(judgmentLimit)} for one judgment, {stopped}.{CostUnreported(spend, request)}",
                FlowLevers.PerformAgain(step.Name),
                spend.Reported,
                cancellationToken);
            return null;
        }
        catch (Exception failure)
        {
            await PauseJudgmentAsync(
                principal, project, flowId, launched,
                $"Step '{step.Name}' got no judgment from agent '{agent.Name}': {failure.Message}{CostUnreported(spend, request)}",
                FlowLevers.PerformAgain(step.Name),
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
                    Flow recorded;
                    try
                    {
                        recorded = await flows.RecordAnswerAsync(
                            principal, project, flowId, step.Name, agentName, answered.Session.Id, agentHash, agent.Model, answer, answered.Usage, cancellationToken);
                    }
                    catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
                    {
                        await SettleRefusedAsync(principal, project, flowId, launched, answered.Usage, refused, cancellationToken);
                        return null;
                    }

                    return new Recording(recorded, answer);
                case JudgmentOutcome.Paused paused:
                    await PauseJudgmentAsync(principal, project, flowId, launched, paused.Reason, paused.Lever, paused.Usage, cancellationToken);
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
    /// its cost: that no cost is recorded for that attempt, and the cost ceiling it ran under, which
    /// bounds what it may have spent. Nothing when every reported attempt carried its cost.
    /// </summary>
    private static string CostUnreported(JudgmentSpend spend, JudgmentRequest request) => spend.CostUnreported
        ? $" The session did not report the cost of its attempt in progress, so none is recorded for it; the spawn ran under a cost ceiling of {Dollars(request.Caps.CostCeilingUsd)}."
        : string.Empty;

    /// <summary>
    /// The judgment a flow's step launched: its step and agent, the session its spawn returned once
    /// it has, and whether the flow recorded that session, which is when the visit expects it.
    /// </summary>
    private sealed class Launched(string step, string agent)
    {
        public string Step { get; } = step;

        public string Agent { get; } = agent;

        public SessionHandle? Session { get; set; }

        public bool Recorded { get; set; }
    }

    /// <summary>
    /// Pauses the flow for a judgment that gave no answer, recording the <paramref name="usage"/> it
    /// reported. A judgment whose session the flow recorded pauses only while the visit still expects
    /// that session; when the pause is refused, what it spent is settled as a refused answer's is.
    /// </summary>
    private async Task PauseJudgmentAsync(
        Principal principal,
        string project,
        Guid flowId,
        Launched launched,
        string reason,
        string lever,
        Measurement? usage,
        CancellationToken cancellationToken)
    {
        Guid? session = launched is { Recorded: true, Session: { } held } ? held.Id : null;
        try
        {
            await flows.PauseAsync(principal, project, flowId, reason, lever, usage, session, cancellationToken);
        }
        catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
        {
            await SettleRefusedAsync(principal, project, flowId, launched, usage, refused, cancellationToken);
        }
    }

    /// <summary>
    /// A judgment whose answer or pause the flow's rules refused. When the flow is no longer running
    /// (a person paused or stopped it while the agent worked), or its visit no longer expects the
    /// session (a resume came after the launch, and its own pass performs the step again), only what
    /// the judgment spent is recorded. Otherwise the flow pauses quoting the rule, carrying that
    /// usage, so a running flow is never left with no worker and no pause.
    /// </summary>
    private async Task SettleRefusedAsync(
        Principal principal,
        string project,
        Guid flowId,
        Launched launched,
        Measurement? usage,
        DomainException refused,
        CancellationToken cancellationToken)
    {
        Flow? now = await flows.ShowAsync(principal, project, flowId, cancellationToken);
        if (now is { Status: FlowStatus.Running } && !NoLongerExpected(now, launched))
        {
            try
            {
                await flows.PauseAsync(
                    principal, project, flowId,
                    $"Flow could not go on from step '{now.CurrentStep}': {refused.Message}",
                    FlowLevers.PerformAgain(now.CurrentStep),
                    usage,
                    session: null,
                    cancellationToken);
                return;
            }
            catch (DomainException pauseRefused)
            {
                logger.LogError(pauseRefused, "Flow {Flow} could neither record its judgment nor pause: {Rule}", Ids.Short(flowId), refused.Message);
            }
        }
        else
        {
            logger.LogInformation("The judgment of step '{Step}' on flow {Flow} records only what it spent: {Rule}", launched.Step, Ids.Short(flowId), refused.Message);
        }

        await KeepUsageAsync(principal, project, flowId, launched, usage, cancellationToken);
    }

    /// <summary>Whether the flow's current visit to the launched step has moved past the launched session.</summary>
    private static bool NoLongerExpected(Flow flow, Launched launched) =>
        launched.Session is { } session
        && flow.Current is { Kind: StepKind.Judgment } current
        && string.Equals(current.Step, launched.Step, StringComparison.Ordinal)
        && (current.ResumedAt is not null || current.SessionId is Guid held && held != session.Id);

    /// <summary>
    /// Records what a judgment spent when neither its answer nor its pause was recorded. There is
    /// nothing to record when the judgment reported nothing; a refusal is logged, since the flow is
    /// as it should be and only the usage is lost.
    /// </summary>
    private async Task KeepUsageAsync(
        Principal principal, string project, Guid flowId, Launched launched, Measurement? usage, CancellationToken cancellationToken)
    {
        if (usage is null || launched.Session is not { } session)
        {
            return;
        }

        try
        {
            await flows.RecordUsageAsync(principal, project, flowId, launched.Step, launched.Agent, session.Id, usage, cancellationToken);
        }
        catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(refused, "Flow {Flow} could not record what the judgment of step '{Step}' spent.", Ids.Short(flowId), launched.Step);
        }
    }

    /// <summary>Lets a person's pause or stop of the flow end the judgment <paramref name="ended"/> bounds.</summary>
    private void Watch(Guid flowId, CancellationTokenSource ended)
    {
        lock (gate)
        {
            if (workers.TryGetValue(flowId, out Worker? worker))
            {
                worker.Judging = ended;
            }
        }
    }

    private void Unwatch(Guid flowId, CancellationTokenSource ended)
    {
        lock (gate)
        {
            if (workers.TryGetValue(flowId, out Worker? worker) && ReferenceEquals(worker.Judging, ended))
            {
                worker.Judging = null;
            }
        }
    }

    /// <summary>
    /// What is left of the budgets a judgment is performed under. <see cref="Spent"/> is the reason a
    /// judgment may not be performed because a budget is spent, or null when none is;
    /// <see cref="Ceiling"/> is the cost ceiling its spawn carries otherwise.
    /// </summary>
    private readonly record struct Budget(string? Spent, decimal Ceiling);

    /// <summary>
    /// The budgets a judgment is performed under: the flow's own recorded cost against its contour's
    /// budget, then, for a flow with a parent, the recorded cost of every flow of its chain against
    /// the budget of the contour its root flow runs. Either reached is the reason no judgment is
    /// performed; otherwise the ceiling is the smaller of the two remainders. The chain is summed by
    /// reading its flows, which all run on this flow's parcel.
    /// </summary>
    private async Task<Budget> BudgetAsync(
        Principal principal, string project, Flow flow, Contour contour, CancellationToken cancellationToken)
    {
        if (flow.Cost >= contour.Budget)
        {
            return new Budget(
                $"Flow {Ids.Short(flow.Id)} has recorded a cost of {Dollars(flow.Cost)}, which reaches the budget of {Dollars(contour.Budget)} of contour '{contour.Name}'; a flow performs no judgment once its recorded cost reaches its contour's budget.",
                0m);
        }

        decimal flowRemainder = contour.Budget - flow.Cost;

        // A flow with no parent is its own chain, already held to its own contour's budget above.
        if (flow.ParentFlowId is null)
        {
            return new Budget(null, flowRemainder);
        }

        IReadOnlyList<Flow> chain = await flows.ChainOfAsync(principal, project, flow, cancellationToken);
        Flow root = chain.FirstOrDefault(f => f.Id == flow.RootFlowId)
            ?? throw new DomainException($"Flow {Ids.Short(flow.Id)} names flow {Ids.Short(flow.RootFlowId)} as the root of its chain, which its parcel does not hold.");
        Contour rootContour = await flows.ContourOfAsync(principal, project, root, cancellationToken);
        decimal chainCost = chain.Sum(f => f.Cost);
        return chainCost >= rootContour.Budget
            ? new Budget(
                $"The chain of flow {Ids.Short(flow.Id)}, rooted at flow {Ids.Short(root.Id)}, has recorded a cost of {Dollars(chainCost)} across its flows, which reaches the budget of {Dollars(rootContour.Budget)} of contour '{rootContour.Name}' that its root flow runs; a chain performs no judgment once its recorded cost reaches that budget.",
                0m)
            : new Budget(null, Math.Min(flowRemainder, rootContour.Budget - chainCost));
    }

    private static string Dollars(decimal amount) => string.Create(CultureInfo.InvariantCulture, $"${amount:0.00##}");

    /// <summary>Performs the step's action and records its result, or pauses; null when the flow paused.</summary>
    private async Task<Recording?> ActAsync(
        Principal principal, string project, Flow flow, Contour contour, ContourStep step, bool resumed, CancellationToken cancellationToken)
    {
        var request = new ActionRequest(principal, project, flow.Id, flow.ParcelId, contour, step, resumed);
        ActionOutcome outcome;
        try
        {
            outcome = await actions.PerformAsync(request, cancellationToken);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            outcome = new ActionOutcome.Paused(
                $"Step '{step.Name}' could not perform the action '{step.Action}': {failure.Message}",
                FlowLevers.PerformAgain(step.Name));
        }

        switch (outcome)
        {
            case ActionOutcome.Done done:
                Flow recorded = await flows.RecordResultAsync(principal, project, flow.Id, step.Name, step.Action ?? string.Empty, done.Result, cancellationToken);
                return new Recording(recorded, done.Result);
            case ActionOutcome.Paused paused:
                await PauseAsync(principal, project, flow.Id, paused.Reason, paused.Lever, cancellationToken);
                return null;
            default:
                throw new InvalidOperationException($"An action ended in an outcome the runner does not know: {outcome.GetType().Name}.");
        }
    }

    /// <summary>Applies the interpreter's next move for the recorded answer or result; true when the flow entered another step.</summary>
    private async Task<bool> RouteAsync(
        Principal principal, string project, Flow flow, Contour contour, ContourStep step, JsonElement recorded, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonSerializer.SerializeToDocument(recorded);
        FlowMove move = Interpreter.Decide(contour, flow.Position, document);
        switch (move)
        {
            case FlowMove.GoTo goTo:
                await flows.StartStepAsync(principal, project, flow.Id, goTo.Step, cancellationToken);
                return true;
            case FlowMove.End end:
                await flows.EndAsync(principal, project, flow.Id, end.Outcome, cancellationToken);
                return false;
            case FlowMove.Pause pause:
                await PauseAsync(principal, project, flow.Id, pause.Reason, FlowLevers.PerformAgain(step.Name), cancellationToken);
                return false;
            default:
                throw new InvalidOperationException($"The interpreter returned a move the runner does not know: {move.GetType().Name}.");
        }
    }

    private Task PauseAsync(Principal principal, string project, Guid flowId, string reason, string lever, CancellationToken cancellationToken) =>
        flows.PauseAsync(principal, project, flowId, reason, lever, cancellationToken);

    /// <summary>
    /// A recording the flow's rules refused. When the flow is no longer running (stopped while
    /// its step was performed, say) nothing more is recorded; otherwise it pauses quoting the rule,
    /// so a running flow is never left with no worker and no pause.
    /// </summary>
    private async Task PauseOnRefusalAsync(Principal principal, string project, Flow flow, DomainException refused, CancellationToken cancellationToken)
    {
        Flow? now = await flows.ShowAsync(principal, project, flow.Id, cancellationToken);
        if (now is not { Status: FlowStatus.Running })
        {
            logger.LogInformation("Flow {Flow} is no longer running, so its step records nothing: {Rule}", Ids.Short(flow.Id), refused.Message);
            return;
        }

        try
        {
            await PauseAsync(
                principal, project, flow.Id,
                $"Flow could not go on from step '{now.CurrentStep}': {refused.Message}",
                FlowLevers.PerformAgain(now.CurrentStep),
                cancellationToken);
        }
        catch (DomainException pauseRefused)
        {
            logger.LogError(pauseRefused, "Flow {Flow} could neither record its step nor pause: {Rule}", Ids.Short(flow.Id), refused.Message);
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

    private static string Describe(TimeSpan limit) =>
        limit >= TimeSpan.FromMinutes(1) && limit.Seconds == 0 && limit.Milliseconds == 0
            ? $"{limit.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes"
            : $"{limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds";

    private sealed class Worker
    {
        /// <summary>Completes when this worker stops advancing the flow.</summary>
        public Task Idle { get; set; } = Task.CompletedTask;

        /// <summary>Another request arrived while this worker ran, so it reads the flow once more before it finishes.</summary>
        public bool Again { get; set; }

        /// <summary>Cancelled by a person's pause or stop to end the judgment this worker runs; null while it runs none.</summary>
        public CancellationTokenSource? Judging { get; set; }
    }
}
