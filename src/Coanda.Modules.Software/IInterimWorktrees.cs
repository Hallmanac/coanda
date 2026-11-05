namespace Coanda.Modules.Software;

/// <summary>
/// The interim seam where the daemon and the module meet outside the module API, which the host hands
/// the module when it builds it. It carries exactly three things and is held to them by a test. The
/// daemon's request to remove a flow run's worktree and its start-up sweep reach the module through
/// the handlers the module registers here, until the feed delivers the flow-run-ended and
/// flow-run-stopped events the module reacts to instead. The worktree step's wait for a holding flow
/// run's worker to return has no call in the module API yet; it needs one before the module leaves
/// the daemon's process.
/// </summary>
public interface IInterimWorktrees
{
    /// <summary>
    /// Registers what the daemon's request to remove a flow run's worktree does: given the project and
    /// the flow run, it removes the flow run's worktree and says whether one was removed.
    /// </summary>
    void OnRemove(Func<string, Guid, CancellationToken, Task<bool>> remove);

    /// <summary>
    /// Registers what the daemon's start-up sweep does: given whether a project's flow run has ended or
    /// been stopped, it removes the worktree of every such flow run the node's clones list.
    /// </summary>
    void OnSweep(Func<Func<string, Guid, CancellationToken, Task<bool>>, CancellationToken, Task> sweep);

    /// <summary>Completes once the worker advancing the flow run <paramref name="flowRunId"/> has returned, at once when none is.</summary>
    Task WorkerReturnedAsync(Guid flowRunId, CancellationToken cancellationToken);
}
