using System.Collections.Concurrent;

namespace Coanda.Modules.Software.Repositories;

/// <summary>
/// The flow runs the module knows to have ended or been stopped with nothing of the core's still running
/// in their work paths, for as long as the module runs: each whose ended or stopped feed delivery has
/// reached it, which the core sends only once the flow run's worker has returned and any stop call on its
/// dispatch has been made, and each its start-up sweep found ended or stopped, whose end was recorded
/// before this start. Each is noted before its worktree's removal is tried, so a removal that fails still
/// leaves the flow run known, and the worktree step takes such a flow run's worktree over at once.
/// </summary>
public sealed class FlowRunEnds
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> ends = new();

    /// <summary>Notes that <paramref name="flowRunId"/> has ended or been stopped and nothing of the core's runs for it, releasing any wait on it.</summary>
    public void Note(Guid flowRunId) => Of(flowRunId).TrySetResult();

    /// <summary>Completes once <paramref name="flowRunId"/> is noted, at once when it already is.</summary>
    public Task ReachedAsync(Guid flowRunId, CancellationToken cancellationToken) => Of(flowRunId).Task.WaitAsync(cancellationToken);

    private TaskCompletionSource Of(Guid flowRunId) =>
        ends.GetOrAdd(flowRunId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
