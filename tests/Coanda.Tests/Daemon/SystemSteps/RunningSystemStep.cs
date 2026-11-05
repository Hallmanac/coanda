using System.Text.Json;
using Coanda.Core.Flows;
using Coanda.Daemon.SystemSteps;

namespace Coanda.Tests.Daemon.SystemSteps;

/// <summary>
/// A system step that lives only in the tests: it answers running to every dispatch unless told
/// otherwise, answers running to a status unless told otherwise, and records every dispatch, status,
/// and stop it is asked, in order. A test registers it through <c>DaemonOptions.SystemSteps</c>.
/// </summary>
internal sealed class RunningSystemStep : IFlowSystemStep
{
    public const string StepName = "test/answers-running";

    private readonly Lock gate = new();
    private readonly List<SystemStepDispatch> dispatches = [];
    private readonly List<Guid> statuses = [];
    private readonly List<Guid> stops = [];

    public string Name => StepName;

    /// <summary>How a dispatch is answered; running unless a test sets it.</summary>
    public Func<SystemStepDispatch, CancellationToken, Task<SystemStepAnswer>> Answer { get; set; } =
        (_, _) => Task.FromResult<SystemStepAnswer>(new SystemStepAnswer.Running());

    /// <summary>How a status is answered; running unless a test sets it. One that throws makes the status call throw.</summary>
    public Func<Guid, CancellationToken, Task<SystemStepStatus>> Status { get; set; } =
        (_, _) => Task.FromResult<SystemStepStatus>(new SystemStepStatus.Running());

    /// <summary>A status answer that is already in hand.</summary>
    public static Func<Guid, CancellationToken, Task<SystemStepStatus>> Answering(SystemStepStatus status) =>
        (_, _) => Task.FromResult(status);

    /// <summary>Runs as a stop is asked, after it is recorded.</summary>
    public Action<Guid>? OnStop { get; set; }

    public IReadOnlyList<SystemStepDispatch> Dispatches
    {
        get
        {
            lock (gate)
            {
                return [.. dispatches];
            }
        }
    }

    public IReadOnlyList<Guid> Statuses
    {
        get
        {
            lock (gate)
            {
                return [.. statuses];
            }
        }
    }

    public IReadOnlyList<Guid> Stops
    {
        get
        {
            lock (gate)
            {
                return [.. stops];
            }
        }
    }

    /// <summary>A flow of one step naming this system step, which ends on a done verdict.</summary>
    public static Flow FlowNamed(string name = "dispatching") => new()
    {
        Name = name,
        Accepts = ["task"],
        Outcome = "dispatched",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            new FlowStep
            {
                Name = "work",
                SystemStep = StepName,
                Arguments = new Dictionary<string, JsonElement> { ["note"] = FlowValue.Of("as the author wrote it") },
                Transitions = [new FlowTransition { Field = SystemStepAnswer.Verdict, Value = FlowValue.Of(SystemStepAnswer.DoneVerdict), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    /// <summary>The done answer this step's flow ends on.</summary>
    public static SystemStepAnswer.Done Done() => SystemStepAnswer.DoneWith(StepName, ("from", "test"));

    public async ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            dispatches.Add(request);
        }

        return await Answer(request, cancellationToken);
    }

    public async ValueTask<SystemStepStatus> StatusAsync(Guid dispatchId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            statuses.Add(dispatchId);
        }

        return await Status(dispatchId, cancellationToken);
    }

    public ValueTask StopAsync(Guid dispatchId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            stops.Add(dispatchId);
        }

        OnStop?.Invoke(dispatchId);
        return ValueTask.CompletedTask;
    }
}
