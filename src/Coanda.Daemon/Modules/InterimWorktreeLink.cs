using Coanda.Daemon.FlowRuns;
using Coanda.Modules.Software;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The daemon's side of the interim seam it shares with the software module (<see cref="IInterimWorktrees"/>):
/// it holds the removal and the sweep the module registers, which the flow run advancer asks for when a
/// flow run ends or is stopped and when the daemon starts, and it answers the worktree step's wait for a
/// holding flow run's worker from the advancer's workers. Until the module registers its handlers, a
/// removal removes nothing and a sweep sweeps nothing.
/// </summary>
public sealed class InterimWorktreeLink(FlowRunWorkers workers) : IInterimWorktrees
{
    private Func<string, Guid, CancellationToken, Task<bool>>? remove;
    private Func<Func<string, Guid, CancellationToken, Task<bool>>, CancellationToken, Task>? sweep;

    public void OnRemove(Func<string, Guid, CancellationToken, Task<bool>> remove) => this.remove = remove;

    public void OnSweep(Func<Func<string, Guid, CancellationToken, Task<bool>>, CancellationToken, Task> sweep) => this.sweep = sweep;

    public Task WorkerReturnedAsync(Guid flowRunId, CancellationToken cancellationToken) => workers.ReturnedAsync(flowRunId, cancellationToken);

    /// <summary>Removes the flow run's worktree, and says whether one was removed.</summary>
    public Task<bool> RemoveAsync(string project, Guid flowRunId, CancellationToken cancellationToken) =>
        remove is { } registered
            ? registered(project, flowRunId, cancellationToken)
            : Task.FromResult(false);

    /// <summary>Removes the worktree of every flow run <paramref name="isFinal"/> says has ended or been stopped.</summary>
    public Task SweepAsync(Func<string, Guid, CancellationToken, Task<bool>> isFinal, CancellationToken cancellationToken) =>
        sweep is { } registered
            ? registered(isFinal, cancellationToken)
            : Task.CompletedTask;
}
