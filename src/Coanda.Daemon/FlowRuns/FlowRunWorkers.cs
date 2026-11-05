namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// The flow run advancer's workers as the module feed sees them: which flow runs have a worker advancing
/// them or a person's stop being saved, and a wait for both to be done. The feed holds a flow run's ended
/// or stopped delivery on that wait, so no module acts on a flow run's end while a step of it may still be
/// running in its work path or a dispatch it holds is still to be told to stop.
/// </summary>
/// <remarks>
/// A stop is saved before the advancer is told of it, and the worker the advancer then starts is what
/// calls stop on a dispatch the flow run holds. <see cref="Stopping"/> covers the moment between: the
/// stop is marked before it is saved and unmarked only once the advancer has been told, by which time
/// that worker is registered here, so the wait never finds neither.
/// </remarks>
public sealed class FlowRunWorkers
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Task> running = [];
    private readonly Dictionary<Guid, Marks> stopping = [];

    /// <summary>
    /// Completes once no worker is advancing <paramref name="flowRunId"/> and no stop of it is being
    /// saved, at once when neither is. A worker or a stop that begins while it waits is waited on too.
    /// </summary>
    public async Task ReturnedAsync(Guid flowRunId, CancellationToken cancellationToken)
    {
        while (Pending(flowRunId) is { } pending)
        {
            await pending.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Marks a person's stop of <paramref name="flowRunId"/> as being saved until the mark is disposed,
    /// which its caller does once the advancer has been told of the stop or the stop was refused.
    /// </summary>
    public IDisposable Stopping(Guid flowRunId)
    {
        lock (gate)
        {
            if (!stopping.TryGetValue(flowRunId, out Marks? marks))
            {
                marks = new Marks();
                stopping.Add(flowRunId, marks);
            }

            marks.Count++;
            return new Mark(this, flowRunId);
        }
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

    /// <summary>What the wait for <paramref name="flowRunId"/> is still to wait on, or null when nothing.</summary>
    private Task? Pending(Guid flowRunId)
    {
        lock (gate)
        {
            Task? worker = running.GetValueOrDefault(flowRunId);
            Task? stop = stopping.GetValueOrDefault(flowRunId)?.Done.Task;
            return (worker, stop) switch
            {
                (null, null) => null,
                ({ } only, null) => only,
                (null, { } only) => only,
                ({ } both, { } and) => Task.WhenAll(both, and),
            };
        }
    }

    private void Unmark(Guid flowRunId)
    {
        lock (gate)
        {
            if (stopping.TryGetValue(flowRunId, out Marks? marks) && --marks.Count == 0)
            {
                stopping.Remove(flowRunId);
                marks.Done.TrySetResult();
            }
        }
    }

    private sealed class Marks
    {
        public int Count { get; set; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Mark(FlowRunWorkers workers, Guid flowRunId) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                workers.Unmark(flowRunId);
            }
        }
    }
}
