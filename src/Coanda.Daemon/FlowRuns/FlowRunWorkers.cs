namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// The flow run advancer's workers as the system steps see them: which flow runs have a worker advancing them, and
/// a wait for one to return. The system steps are built before the advancer and handed to it, so they share
/// this rather than the advancer itself.
/// </summary>
public sealed class FlowRunWorkers
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Task> running = [];

    /// <summary>Completes once the worker advancing <paramref name="flowRunId"/> has returned, at once when none is.</summary>
    public Task ReturnedAsync(Guid flowRunId, CancellationToken cancellationToken)
    {
        Task? idle;
        lock (gate)
        {
            idle = running.GetValueOrDefault(flowRunId);
        }

        return idle?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>The runner started a worker for <paramref name="flowRunId"/>, which completes <paramref name="idle"/> when it returns.</summary>
    internal void Started(Guid flowRunId, Task idle)
    {
        lock (gate)
        {
            running[flowRunId] = idle;
        }
    }

    /// <summary>The runner's worker for <paramref name="flowRunId"/> has done its last work and is returning.</summary>
    internal void Returned(Guid flowRunId)
    {
        lock (gate)
        {
            running.Remove(flowRunId);
        }
    }
}
