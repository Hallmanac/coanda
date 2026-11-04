using System.Globalization;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flights;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Parcels;
using Fisher;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Flights;

/// <summary>
/// Advances live flights on the daemon's own workers: performs the current step through the
/// executor or an action, records its result on the flight, asks the interpreter for the next
/// move, and applies it, until the flight ends, pauses, or stops. At most one worker advances a
/// flight; a request for a flight already being advanced is folded into that worker, which reads
/// the flight once more before it finishes. Before any step, a flight deeper in its chain than its
/// flow's follow allows pauses; before a judgment, a flight whose recorded cost has reached its flow's
/// budget, or whose chain's recorded cost has reached the budget of the flow its root runs, pauses.
/// Both checks run on every pass, so a resume of such a pause pauses again rather than overriding it.
/// A resumed judgment step spawns another agent, so it pauses once the flight's spawns reach its
/// flow's spawn cap, whether the visit resumed had recorded an answer or paused without one.
/// </summary>
/// <remarks>
/// Every write is made as the flight's own principal, of the Agent role, through a flight service
/// of the runner's own: the service the daemon registers hands resumes to this runner, so the
/// runner cannot also take that one without a cycle. A daemon stop cancels the step in progress
/// and records nothing for it, leaving the flight live and unpaused so the next start performs the
/// step again. A step recorded before the stop but not yet routed is routed without being
/// performed again.
/// </remarks>
public sealed class FlightRunner : IFlightAdvancer, IHostedService, IDisposable
{
    /// <summary>The principal that reads the node's live flights when the daemon starts.</summary>
    public static readonly Principal ScanPrincipal = new("coanda-runner", Role.Agent);

    private readonly FlightService flights;
    private readonly ParcelService parcels;
    private readonly DefinitionService definitions;
    private readonly FlowActions actions;
    private readonly IAgentExecutor executor;
    private readonly NodeHome home;
    private readonly TimeProvider clock;
    private readonly TimeSpan judgmentLimit;
    private readonly ILogger<FlightRunner> logger;
    private readonly CancellationTokenSource stopping = new();
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Worker> workers = [];
    private bool stopRequested;

    public FlightRunner(
        IDocumentStore store,
        ParcelService parcels,
        DefinitionService definitions,
        FlowActions actions,
        IAgentExecutor executor,
        NodeHome home,
        TimeProvider clock,
        FlightRunnerOptions options,
        ILogger<FlightRunner> logger)
    {
        flights = new FlightService(store, clock);
        this.parcels = parcels;
        this.definitions = definitions;
        this.actions = actions;
        this.executor = executor;
        this.home = home;
        this.clock = clock;
        judgmentLimit = options.JudgmentLimit;
        this.logger = logger;
    }

    /// <summary>The principal every write made while advancing a flight carries: the Agent role, named for the flight.</summary>
    public static Principal PrincipalFor(Guid flightId) => new($"flight:{flightId}", Role.Agent);

    public Task RequestAdvance(string project, Guid flightId)
    {
        lock (gate)
        {
            if (stopRequested)
            {
                return Task.CompletedTask;
            }

            if (workers.TryGetValue(flightId, out Worker? running))
            {
                running.Again = true;
                return running.Idle;
            }

            var worker = new Worker();
            workers.Add(flightId, worker);
            worker.Idle = Task.Run(() => WorkAsync(project, flightId, worker, stopping.Token));
            return worker.Idle;
        }
    }

    /// <summary>Requests an advance of every live flight on the node; a paused one is read and left as it is.</summary>
    public async Task AdvanceLiveAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<(string Project, Guid FlightId)> live = await flights.ListLiveAsync(ScanPrincipal, cancellationToken);
        foreach ((string project, Guid flightId) in live)
        {
            _ = RequestAdvance(project, flightId);
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
            logger.LogWarning("The daemon stopped before {Count} flight workers finished giving up their steps.", running.Length);
        }
    }

    public void Dispose() => stopping.Dispose();

    private async Task WorkAsync(string project, Guid flightId, Worker worker, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await AdvanceAsync(project, flightId, cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // A daemon stop: the step in progress records nothing and is performed again on the next start.
            }
            catch (Exception failure)
            {
                logger.LogError(failure, "Advancing flight {Flight} in project {Project} failed; it is left as it was recorded.", Ids.Short(flightId), project);
            }

            lock (gate)
            {
                if (!worker.Again || cancellationToken.IsCancellationRequested)
                {
                    workers.Remove(flightId);
                    return;
                }

                worker.Again = false;
            }
        }
    }

    private async Task AdvanceAsync(string project, Guid flightId, CancellationToken cancellationToken)
    {
        Principal principal = PrincipalFor(flightId);
        bool moving = true;
        while (moving)
        {
            Flight? flight = await flights.ShowAsync(principal, project, flightId, cancellationToken);
            if (flight is not { Status: FlightStatus.Running })
            {
                return;
            }

            try
            {
                moving = await StepAsync(principal, project, flight, cancellationToken);
            }
            catch (DomainException refused) when (!cancellationToken.IsCancellationRequested)
            {
                await PauseOnRefusalAsync(principal, project, flight, refused, cancellationToken);
                return;
            }
        }
    }

    /// <summary>Takes the flight one step on; true when it entered another step and is still running.</summary>
    private async Task<bool> StepAsync(Principal principal, string project, Flight flight, CancellationToken cancellationToken)
    {
        FlowDefinition flow = await flights.FlowOfAsync(principal, project, flight, cancellationToken);
        FlightStepEntry current = flight.Current
            ?? throw new DomainException($"Flight {Ids.Short(flight.Id)} has entered no step, so there is nothing to perform.");
        FlowStep step = flow.Step(current.Step)
            ?? throw new DomainException($"Flow '{flow.Name}' has no step '{current.Step}', so flight {Ids.Short(flight.Id)} cannot perform it.");

        if (flow.Follow is { } follow && flight.Depth > follow.DepthCap)
        {
            await PauseAsync(
                principal, project, flight.Id,
                $"Flight {Ids.Short(flight.Id)} is at depth {flight.Depth} of its chain, past the depth cap of {follow.DepthCap} on the follow of flow '{flow.Name}'; a flight deeper than its follow's depth cap performs no step.",
                FlightLevers.DepthCapReached(flow.Name),
                cancellationToken);
            return false;
        }

        // A resumed judgment spawns another agent, whether its visit recorded or not, once its
        // counted spawn is used: not when the visit paused before it launched, and not when this
        // resume's spawn is already counted (a restart performing the resumed visit again). A
        // judgment the interpreter entered within the cap is performed once, so it is not checked.
        bool spawnsAgain = current.SpawnsAgainOnResume;
        if (spawnsAgain && flight.Spawns >= flow.SpawnCap)
        {
            await PauseAsync(
                principal, project, flight.Id,
                $"Flow '{flow.Name}' reached its spawn cap of {flow.SpawnCap}; performing judgment step '{step.Name}' again would spawn another agent.",
                FlightLevers.SpawnCapReached(flow.Name),
                cancellationToken);
            return false;
        }

        // A resume after this visit recorded (a pause the interpreter decided) performs the step
        // again as a new visit; a recording with no resume since is routed, never performed again.
        if (current.IsRecorded && current.ResumedAt is not null)
        {
            await flights.StartStepAsync(principal, project, flight.Id, step.Name, cancellationToken);
            return true;
        }

        Recording? recorded = current.IsRecorded
            ? (current.Answer ?? current.Result) is JsonElement held ? new Recording(flight, held) : null
            : step.IsJudgment
                ? await JudgeAsync(principal, project, flight, flow, step, spawnsAgain, cancellationToken)
                : await ActAsync(principal, project, flight, flow, step, current.ResumedAt is not null, cancellationToken);

        // Routed against the flight as the recording left it, so a spawn counted on this pass is in the position.
        return recorded is Recording recording
            && await RouteAsync(principal, project, recording.Flight, flow, step, recording.Value, cancellationToken);
    }

    /// <summary>What a step recorded, with the flight as that recording left it.</summary>
    private readonly record struct Recording(Flight Flight, JsonElement Value);

    /// <summary>
    /// Runs the step's judgment and records its answer, or pauses; null when the flight paused. When
    /// <paramref name="spawnsAgain"/>, a resume performs again a visit that launched and paused
    /// without an answer, so one more spawn is counted on that visit once the budget checks have
    /// passed. The launch itself is recorded just before the agent runs, so a pause after the count
    /// and before the launch (an agent that does not resolve, a parcel that does not load) leaves
    /// that spawn unused, and the next resume uses it rather than counting another.
    /// </summary>
    private async Task<Recording?> JudgeAsync(
        Principal principal, string project, Flight flight, FlowDefinition flow, FlowStep step, bool spawnsAgain, CancellationToken cancellationToken)
    {
        string? spent = await BudgetSpentAsync(principal, project, flight, flow, cancellationToken);
        if (spent is not null)
        {
            await PauseAsync(principal, project, flight.Id, spent, FlightLevers.BudgetReached(), cancellationToken);
            return null;
        }

        if (spawnsAgain)
        {
            flight = await flights.RespawnAsync(principal, project, flight.Id, step.Name, cancellationToken);
        }

        string agentName = step.Agent ?? string.Empty;
        Definition? published = await definitions.ResolveAgentAsync(principal, project, agentName, cancellationToken);
        if (published is null)
        {
            await PauseAsync(
                principal, project, flight.Id,
                $"Step '{step.Name}' names the agent '{agentName}', which resolution does not return as a published agent for project '{project}'.",
                FlightLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }

        AgentDefinition agent = AgentDefinition.FromJson(published.Content);
        Parcel parcel = await parcels.ShowAsync(principal, project, flight.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(flight.ParcelId)} in project '{project}'.");
        var request = new JudgmentRequest(
            agent,
            ContextAssembler.Assemble(parcel, step),
            home.FlightWorkPath(project, flight.Id),
            new SpawnCaps(flow.Budget, judgmentLimit),
            step.AnswerSchema ?? string.Empty,
            CredentialProfile: null);

        flight = await flights.SpawnedAsync(principal, project, flight.Id, step.Name, cancellationToken);
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
                principal, project, flight.Id,
                $"Step '{step.Name}' ran past the wall-clock limit of {Describe(judgmentLimit)} for one judgment, {stopped}.",
                FlightLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }
        catch (Exception failure)
        {
            await PauseAsync(
                principal, project, flight.Id,
                $"Step '{step.Name}' got no judgment from agent '{agent.Name}': {failure.Message}",
                FlightLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }

        try
        {
            switch (outcome)
            {
                case JudgmentOutcome.Answered answered:
                    JsonElement answer = Parse(answered.Answer);
                    Flight recorded = await flights.RecordAnswerAsync(
                        principal, project, flight.Id, step.Name, agentName, published.ContentHash, agent.Model, answer, answered.Usage, cancellationToken);
                    return new Recording(recorded, answer);
                case JudgmentOutcome.Paused paused:
                    await flights.PauseAsync(principal, project, flight.Id, paused.Reason, paused.Lever, paused.Usage, cancellationToken);
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
    /// the flight's own recorded cost against its flow's budget, then the recorded cost of every
    /// flight of its chain against the budget of the flow its root flight runs. The chain is summed
    /// by reading its flights, which all run on this flight's parcel.
    /// </summary>
    private async Task<string?> BudgetSpentAsync(
        Principal principal, string project, Flight flight, FlowDefinition flow, CancellationToken cancellationToken)
    {
        if (flight.Cost >= flow.Budget)
        {
            return $"Flight {Ids.Short(flight.Id)} has recorded a cost of {Dollars(flight.Cost)}, which reaches the budget of {Dollars(flow.Budget)} of flow '{flow.Name}'; a flight performs no judgment once its recorded cost reaches its flow's budget.";
        }

        // A flight with no parent is its own chain, already held to its own flow's budget above.
        if (flight.ParentFlightId is null)
        {
            return null;
        }

        IReadOnlyList<Flight> chain = await flights.ChainOfAsync(principal, project, flight, cancellationToken);
        Flight root = chain.FirstOrDefault(f => f.Id == flight.RootFlightId)
            ?? throw new DomainException($"Flight {Ids.Short(flight.Id)} names flight {Ids.Short(flight.RootFlightId)} as the root of its chain, which its parcel does not hold.");
        FlowDefinition rootFlow = await flights.FlowOfAsync(principal, project, root, cancellationToken);
        decimal chainCost = chain.Sum(f => f.Cost);
        return chainCost >= rootFlow.Budget
            ? $"The chain of flight {Ids.Short(flight.Id)}, rooted at flight {Ids.Short(root.Id)}, has recorded a cost of {Dollars(chainCost)} across its flights, which reaches the budget of {Dollars(rootFlow.Budget)} of flow '{rootFlow.Name}' that its root flight runs; a chain performs no judgment once its recorded cost reaches that budget."
            : null;
    }

    private static string Dollars(decimal amount) => string.Create(CultureInfo.InvariantCulture, $"${amount:0.00##}");

    /// <summary>Performs the step's action and records its result, or pauses; null when the flight paused.</summary>
    private async Task<Recording?> ActAsync(
        Principal principal, string project, Flight flight, FlowDefinition flow, FlowStep step, bool resumed, CancellationToken cancellationToken)
    {
        var request = new ActionRequest(principal, project, flight.Id, flight.ParcelId, flow, step, resumed);
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
                FlightLevers.PerformAgain(step.Name));
        }

        switch (outcome)
        {
            case ActionOutcome.Done done:
                Flight recorded = await flights.RecordResultAsync(principal, project, flight.Id, step.Name, step.Action ?? string.Empty, done.Result, cancellationToken);
                return new Recording(recorded, done.Result);
            case ActionOutcome.Paused paused:
                await PauseAsync(principal, project, flight.Id, paused.Reason, paused.Lever, cancellationToken);
                return null;
            default:
                throw new InvalidOperationException($"An action ended in an outcome the runner does not know: {outcome.GetType().Name}.");
        }
    }

    /// <summary>Applies the interpreter's next move for the recorded answer or result; true when the flight entered another step.</summary>
    private async Task<bool> RouteAsync(
        Principal principal, string project, Flight flight, FlowDefinition flow, FlowStep step, JsonElement recorded, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonSerializer.SerializeToDocument(recorded);
        FlightMove move = Interpreter.Decide(flow, flight.Position, document);
        switch (move)
        {
            case FlightMove.GoTo goTo:
                await flights.StartStepAsync(principal, project, flight.Id, goTo.Step, cancellationToken);
                return true;
            case FlightMove.End end:
                await flights.EndAsync(principal, project, flight.Id, end.Outcome, cancellationToken);
                return false;
            case FlightMove.Pause pause:
                await PauseAsync(principal, project, flight.Id, pause.Reason, FlightLevers.PerformAgain(step.Name), cancellationToken);
                return false;
            default:
                throw new InvalidOperationException($"The interpreter returned a move the runner does not know: {move.GetType().Name}.");
        }
    }

    private Task PauseAsync(Principal principal, string project, Guid flightId, string reason, string lever, CancellationToken cancellationToken) =>
        flights.PauseAsync(principal, project, flightId, reason, lever, cancellationToken);

    /// <summary>
    /// A recording the flight's rules refused. When the flight is no longer running (stopped while
    /// its step was performed, say) nothing more is recorded; otherwise it pauses quoting the rule,
    /// so a running flight is never left with no worker and no pause.
    /// </summary>
    private async Task PauseOnRefusalAsync(Principal principal, string project, Flight flight, DomainException refused, CancellationToken cancellationToken)
    {
        Flight? now = await flights.ShowAsync(principal, project, flight.Id, cancellationToken);
        if (now is not { Status: FlightStatus.Running })
        {
            logger.LogInformation("Flight {Flight} is no longer running, so its step records nothing: {Rule}", Ids.Short(flight.Id), refused.Message);
            return;
        }

        try
        {
            await PauseAsync(
                principal, project, flight.Id,
                $"Flight could not go on from step '{now.CurrentStep}': {refused.Message}",
                FlightLevers.PerformAgain(now.CurrentStep),
                cancellationToken);
        }
        catch (DomainException pauseRefused)
        {
            logger.LogError(pauseRefused, "Flight {Flight} could neither record its step nor pause: {Rule}", Ids.Short(flight.Id), refused.Message);
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
        /// <summary>Completes when this worker stops advancing the flight.</summary>
        public Task Idle { get; set; } = Task.CompletedTask;

        /// <summary>Another request arrived while this worker ran, so it reads the flight once more before it finishes.</summary>
        public bool Again { get; set; }
    }
}
