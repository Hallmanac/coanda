namespace Coanda.Daemon.Flows;

/// <summary>
/// The flow runner's workers as the actions see them: which flows have a worker advancing them, and
/// a wait for one to return. The actions are built before the runner and handed to it, so they share
/// this rather than the runner itself.
/// </summary>
public sealed class FlowWorkers
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Task> running = [];

    /// <summary>Completes once the worker advancing <paramref name="flowId"/> has returned, at once when none is.</summary>
    public Task ReturnedAsync(Guid flowId, CancellationToken cancellationToken)
    {
        Task? idle;
        lock (gate)
        {
            idle = running.GetValueOrDefault(flowId);
        }

        return idle?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>The runner started a worker for <paramref name="flowId"/>, which completes <paramref name="idle"/> when it returns.</summary>
    internal void Started(Guid flowId, Task idle)
    {
        lock (gate)
        {
            running[flowId] = idle;
        }
    }

    /// <summary>The runner's worker for <paramref name="flowId"/> has done its last work and is returning.</summary>
    internal void Returned(Guid flowId)
    {
        lock (gate)
        {
            running.Remove(flowId);
        }
    }
}
