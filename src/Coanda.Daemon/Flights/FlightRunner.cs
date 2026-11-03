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
/// move, and applies it, until the flight ends, parks, or stops. At most one worker advances a
/// flight; a request for a flight already being advanced is folded into that worker, which reads
/// the flight once more before it finishes.
/// </summary>
/// <remarks>
/// Every write is made as the flight's own principal, of the Agent role, through a flight service
/// of the runner's own: the service the daemon registers hands resumes to this runner, so the
/// runner cannot also take that one without a cycle. A daemon stop cancels the step in progress
/// and records nothing for it, leaving the flight live and unparked so the next start performs the
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

    /// <summary>Requests an advance of every live flight on the node; a parked one is read and left as it is.</summary>
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
                await ParkOnRefusalAsync(principal, project, flight, refused, cancellationToken);
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

        // A resume after this visit recorded (a park the interpreter decided) performs the step
        // again as a new visit; a recording with no resume since is routed, never performed again.
        if (current.IsRecorded && current.ResumedAt is not null)
        {
            if (step.IsJudgment && flight.Spawns >= flow.SpawnCap)
            {
                await ParkAsync(
                    principal, project, flight.Id,
                    $"Flow '{flow.Name}' reached its spawn cap of {flow.SpawnCap}; performing judgment step '{step.Name}' again would spawn another agent.",
                    FlightLevers.SpawnCapReached(flow.Name),
                    cancellationToken);
                return false;
            }

            await flights.StartStepAsync(principal, project, flight.Id, step.Name, cancellationToken);
            return true;
        }

        JsonElement? recorded = current.IsRecorded
            ? current.Answer ?? current.Result
            : step.IsJudgment
                ? await JudgeAsync(principal, project, flight, flow, step, cancellationToken)
                : await ActAsync(principal, project, flight, flow, step, current.ResumedAt is not null, cancellationToken);

        return recorded is JsonElement document
            && await RouteAsync(principal, project, flight, flow, step, document, cancellationToken);
    }

    /// <summary>Runs the step's judgment and records its answer, or parks; null when the flight parked.</summary>
    private async Task<JsonElement?> JudgeAsync(
        Principal principal, string project, Flight flight, FlowDefinition flow, FlowStep step, CancellationToken cancellationToken)
    {
        string agentName = step.Agent ?? string.Empty;
        Definition? published = await definitions.ResolveAgentAsync(principal, project, agentName, cancellationToken);
        if (published is null)
        {
            await ParkAsync(
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
            await ParkAsync(
                principal, project, flight.Id,
                $"Step '{step.Name}' ran past the wall-clock limit of {Describe(judgmentLimit)} for one judgment, {stopped}.",
                FlightLevers.PerformAgain(step.Name),
                cancellationToken);
            return null;
        }
        catch (Exception failure)
        {
            await ParkAsync(
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
                    await flights.RecordAnswerAsync(
                        principal, project, flight.Id, step.Name, agentName, published.ContentHash, agent.Model, answer, answered.Usage, cancellationToken);
                    return answer;
                case JudgmentOutcome.Parked parked:
                    await ParkAsync(principal, project, flight.Id, parked.Reason, parked.Lever, cancellationToken);
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

    /// <summary>Performs the step's action and records its result, or parks; null when the flight parked.</summary>
    private async Task<JsonElement?> ActAsync(
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
            outcome = new ActionOutcome.Parked(
                $"Step '{step.Name}' could not perform the action '{step.Action}': {failure.Message}",
                FlightLevers.PerformAgain(step.Name));
        }

        switch (outcome)
        {
            case ActionOutcome.Done done:
                await flights.RecordResultAsync(principal, project, flight.Id, step.Name, step.Action ?? string.Empty, done.Result, cancellationToken);
                return done.Result;
            case ActionOutcome.Parked parked:
                await ParkAsync(principal, project, flight.Id, parked.Reason, parked.Lever, cancellationToken);
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
            case FlightMove.Park park:
                await ParkAsync(principal, project, flight.Id, park.Reason, FlightLevers.PerformAgain(step.Name), cancellationToken);
                return false;
            default:
                throw new InvalidOperationException($"The interpreter returned a move the runner does not know: {move.GetType().Name}.");
        }
    }

    private Task ParkAsync(Principal principal, string project, Guid flightId, string reason, string lever, CancellationToken cancellationToken) =>
        flights.ParkAsync(principal, project, flightId, reason, lever, cancellationToken);

    /// <summary>
    /// A recording the flight's rules refused. When the flight is no longer running (stopped while
    /// its step was performed, say) nothing more is recorded; otherwise it parks quoting the rule,
    /// so a running flight is never left with no worker and no park.
    /// </summary>
    private async Task ParkOnRefusalAsync(Principal principal, string project, Flight flight, DomainException refused, CancellationToken cancellationToken)
    {
        Flight? now = await flights.ShowAsync(principal, project, flight.Id, cancellationToken);
        if (now is not { Status: FlightStatus.Running })
        {
            logger.LogInformation("Flight {Flight} is no longer running, so its step records nothing: {Rule}", Ids.Short(flight.Id), refused.Message);
            return;
        }

        try
        {
            await ParkAsync(
                principal, project, flight.Id,
                $"Flight could not go on from step '{now.CurrentStep}': {refused.Message}",
                FlightLevers.PerformAgain(now.CurrentStep),
                cancellationToken);
        }
        catch (DomainException parkRefused)
        {
            logger.LogError(parkRefused, "Flight {Flight} could neither record its step nor park: {Rule}", Ids.Short(flight.Id), refused.Message);
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
