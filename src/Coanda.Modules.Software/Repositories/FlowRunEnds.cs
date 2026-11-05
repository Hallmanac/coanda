using System.Collections.Concurrent;

namespace Coanda.Modules.Software.Repositories;

/// <summary>
/// The flow runs the module knows to have ended or been stopped with nothing of the core's still running
/// in their work paths, and the worktree steps' waits for that: each whose ended or stopped feed delivery
/// has reached it, which the core sends only once the flow run's worker has returned and any stop call on
/// its dispatch has been made, and each its start-up sweep found ended or stopped, whose end was recorded
/// before this start. Each is noted before its worktree's removal is tried, so a removal that fails still
/// leaves the flow run known, and the worktree step takes such a flow run's worktree over at once.
/// </summary>
/// <remarks>
/// It does not grow with every flow run the module sees. A delivery's flow run is forgotten once its
/// worktree is gone, since no step can then find that worktree holding a branch; what stays is a flow
/// run whose removal failed, or one the start-up sweep noted, as many as there were worktrees. A wait
/// that is cancelled before a note takes its entry with it. A step that comes to wait on a flow run after
/// it was forgotten, or whose end never reaches the module, goes on once the core shows that flow run no
/// longer working (<see cref="Steps.WorktreeStep"/>).
/// </remarks>
public sealed class FlowRunEnds
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> ends = new();

    /// <summary>How many flow runs it holds an entry for.</summary>
    internal int Count => ends.Count;

    /// <summary>Notes that <paramref name="flowRunId"/> has ended or been stopped and nothing of the core's runs for it, releasing any wait on it.</summary>
    public void Note(Guid flowRunId) => Of(flowRunId).TrySetResult();

    /// <summary>Forgets <paramref name="flowRunId"/>, whose worktree is gone, so it holds nothing for it.</summary>
    public void Forget(Guid flowRunId) => ends.TryRemove(flowRunId, out _);

    /// <summary>Completes once <paramref name="flowRunId"/> is noted, at once when it already is.</summary>
    public async Task ReachedAsync(Guid flowRunId, CancellationToken cancellationToken)
    {
        TaskCompletionSource end = Of(flowRunId);
        try
        {
            await end.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!end.Task.IsCompleted)
        {
            // Nothing noted it, so the entry this wait added goes with it.
            ends.TryRemove(KeyValuePair.Create(flowRunId, end));
            throw;
        }
    }

    private TaskCompletionSource Of(Guid flowRunId) =>
        ends.GetOrAdd(flowRunId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
