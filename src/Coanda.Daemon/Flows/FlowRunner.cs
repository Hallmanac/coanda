using System.Globalization;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Parcels;
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
/// and records nothing for it, leaving the flow live and unpaused so the next start performs the
/// step again. A step recorded before the stop but not yet routed is routed without being
/// performed again.
/// </remarks>
public sealed class FlowRunner : IFlowAdvancer, IHostedService, IDisposable
{
    /// <summary>The principal that reads the node's live flows when the daemon starts.</summary>
    public static readonly Principal ScanPrincipal = new("coanda-runner", Role.Agent);

    private readonly FlowService flows;
    private readonly ParcelService parcels;
    private readonly DefinitionService definitions;
    private readonly FlowActions actions;
    private readonly IAgentExecutor executor;
    private readonly NodeHome home;
    private readonly TimeProvider clock;
    private readonly TimeSpan judgmentLimit;
    private readonly ILogger<FlowRunner> logger;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Worker> workers = [];
    private bool stopRequested;

    public FlowRunner(
        IDocumentStore store,
        ParcelService parcels,
        DefinitionService definitions,
        FlowActions actions,
        IAgentExecutor executor,
        NodeHome home,
        TimeProvider clock,
        FlowRunnerOptions options,
        ILogger<FlowRunner> logger)
    {
        flows = new FlowService(store, clock);
        this.parcels = parcels;
        this.definitions = definitions;
        this.actions = actions;
        this.executor = executor;
        this.home = home;
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

    /// <summary>Cancels every step in progress and waits for the workers to give up, recording nothing for those steps.</summary>
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

    /// <summary>What a step recorded, with the flow as that recording left it.</summary>
    private readonly record struct Recording(Flow Flow, JsonElement Value);

    /// <summary>
    /// Runs the step's judgment and records its answer, or pauses; null when the flow paused. When
    /// <paramref name="spawnsAgain"/>, a resume performs again a visit that launched and paused
    /// without an answer, so one more spawn is counted on that visit once the budget checks have
    /// passed. The launch itself is recorded just before the agent runs, so a pause after the count
    /// and before the launch (an agent that does not resolve, a parcel that does not load) leaves
    /// that spawn unused, and the next resume uses it rather than counting another.
    /// </summary>
    private async Task<Recording?> JudgeAsync(
        Principal principal, string project, Flow flow, Contour contour, ContourStep step, bool spawnsAgain, CancellationToken cancellationToken)
    {
        string? spent = await BudgetSpentAsync(principal, project, flow, contour, cancellationToken);
        if (spent is not null)
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
        Parcel parcel = await parcels.ShowAsync(principal, project, flow.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(flow.ParcelId)} in project '{project}'.");
        var request = new JudgmentRequest(
            agent,
            ContextAssembler.Assemble(parcel, step),
            home.FlowWorkPath(project, flow.Id),
            new SpawnCaps(contour.Budget, judgmentLimit),
            step.AnswerSchema ?? string.Empty,
            CredentialProfile: null);

        flow = await flows.SpawnedAsync(principal, project, flow.Id, step.Name, cancellationToken);
        using var limit = new CancellationTokenSource(judgmentLimit, clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        JudgmentOutcome outcome;
        try
        {
            outcome = await Judgment.RunAsync(executor, request, bounded.Token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            await PauseAsync(
                principal, project, flow.Id,
                $"Step '{step.Name}' ran past the wall-clock limit of {Describe(judgmentLimit)} for one judgment, {stopped}.",
                FlowLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }
        catch (Exception failure)
        {
            await PauseAsync(
                principal, project, flow.Id,
                $"Step '{step.Name}' got no judgment from agent '{agent.Name}': {failure.Message}",
                FlowLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }

        try
        {
            switch (outcome)
            {
                case JudgmentOutcome.Answered answered:
                    JsonElement answer = Parse(answered.Answer);
                    Flow recorded = await flows.RecordAnswerAsync(
                        principal, project, flow.Id, step.Name, agentName, published.ContentHash, agent.Model, answer, answered.Usage, cancellationToken);
                    return new Recording(recorded, answer);
                case JudgmentOutcome.Paused paused:
                    await flows.PauseAsync(principal, project, flow.Id, paused.Reason, paused.Lever, paused.Usage, cancellationToken);
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
    /// The reason a judgment may not be performed because a budget is spent, or null when none is:
    /// the flow's own recorded cost against its contour's budget, then the recorded cost of every
    /// flow of its chain against the budget of the contour its root flow runs. The chain is summed
    /// by reading its flows, which all run on this flow's parcel.
    /// </summary>
    private async Task<string?> BudgetSpentAsync(
        Principal principal, string project, Flow flow, Contour contour, CancellationToken cancellationToken)
    {
        if (flow.Cost >= contour.Budget)
        {
            return $"Flow {Ids.Short(flow.Id)} has recorded a cost of {Dollars(flow.Cost)}, which reaches the budget of {Dollars(contour.Budget)} of contour '{contour.Name}'; a flow performs no judgment once its recorded cost reaches its contour's budget.";
        }

        // A flow with no parent is its own chain, already held to its own contour's budget above.
        if (flow.ParentFlowId is null)
        {
            return null;
        }

        IReadOnlyList<Flow> chain = await flows.ChainOfAsync(principal, project, flow, cancellationToken);
        Flow root = chain.FirstOrDefault(f => f.Id == flow.RootFlowId)
            ?? throw new DomainException($"Flow {Ids.Short(flow.Id)} names flow {Ids.Short(flow.RootFlowId)} as the root of its chain, which its parcel does not hold.");
        Contour rootContour = await flows.ContourOfAsync(principal, project, root, cancellationToken);
        decimal chainCost = chain.Sum(f => f.Cost);
        return chainCost >= rootContour.Budget
            ? $"The chain of flow {Ids.Short(flow.Id)}, rooted at flow {Ids.Short(root.Id)}, has recorded a cost of {Dollars(chainCost)} across its flows, which reaches the budget of {Dollars(rootContour.Budget)} of contour '{rootContour.Name}' that its root flow runs; a chain performs no judgment once its recorded cost reaches that budget."
            : null;
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
    }
}
