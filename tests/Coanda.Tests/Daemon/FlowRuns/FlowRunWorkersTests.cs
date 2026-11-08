using Coanda.Core;
using Coanda.Daemon.FlowRuns;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The wait the module feed holds a flow run's ended or stopped delivery on: it completes once no worker
/// advances the flow run and no stop of it is being saved, and a worker registered while a stop is being
/// saved is waited on too, so the moment between a stop's save and the advancer hearing of it is covered.
/// </summary>
public sealed class FlowRunWorkersTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public void The_wait_completes_at_once_when_nothing_runs_for_the_flow_run()
    {
        var workers = new FlowRunWorkers();

        Assert.True(workers.ReturnedAsync(Ids.New(), None).IsCompleted);
    }

    [Fact]
    public async Task The_wait_holds_while_a_stop_is_being_saved_and_then_for_the_worker_the_stop_started()
    {
        var workers = new FlowRunWorkers();
        Guid flowRunId = Ids.New();
        var worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable stopping = workers.Stopping(flowRunId);

        Task returned = workers.ReturnedAsync(flowRunId, None);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(returned.IsCompleted, "a stop being saved holds the wait");

        // The advancer is told of the stop and starts a worker before the mark goes.
        workers.Started(flowRunId, worker.Task);
        stopping.Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(returned.IsCompleted, "the worker the stop started holds the wait");

        workers.Returned(flowRunId);
        worker.SetResult();
        await returned.WaitAsync(TestPatience.Bound);
    }

    [Fact]
    public async Task The_wait_holds_until_every_stop_being_saved_is_done_and_a_mark_disposed_twice_counts_once()
    {
        var workers = new FlowRunWorkers();
        Guid flowRunId = Ids.New();
        IDisposable first = workers.Stopping(flowRunId);
        IDisposable second = workers.Stopping(flowRunId);
        Task returned = workers.ReturnedAsync(flowRunId, None);

        first.Dispose();
        first.Dispose();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Assert.False(returned.IsCompleted, "the second stop is still being saved");

        second.Dispose();
        await returned.WaitAsync(TestPatience.Bound);
        Assert.True(workers.ReturnedAsync(flowRunId, None).IsCompleted);
    }
}
