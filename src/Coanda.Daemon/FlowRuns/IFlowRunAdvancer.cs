namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// The daemon's requests of the worker that advances a flow run by its id. The start endpoint, a
/// resume, and the daemon's own start ask for an advance; a person's pause asks for the agent step in
/// progress to end, and a person's stop for the agent step or system step in progress to end and the
/// flow run's worktree to be removed. <see cref="FlowRunAdvancer"/> answers all three.
/// </summary>
public interface IFlowRunAdvancer
{
    /// <summary>
    /// Asks for the flow run to be advanced until it ends, pauses, or stops. A request for a flow run
    /// already being advanced joins that work rather than starting a second worker. The task
    /// completes when no worker is advancing the flow run any more.
    /// </summary>
    Task RequestAdvance(string project, Guid flowRunId);

    /// <summary>
    /// Tells the worker advancing the flow run that a person's pause or stop of it was saved, so the
    /// agent step it is running ends now, its session stopped through the executor, rather than when
    /// the agent answers or the wall-clock limit passes. It returns without waiting for that, and
    /// does nothing when no agent step of the flow run is running.
    /// </summary>
    void EndAgentStep(Guid flowRunId);

    /// <summary>
    /// Tells the worker advancing the flow run that a person's stop of it was saved: the agent step it is
    /// running ends as for <see cref="EndAgentStep"/>, the call on a system step it is making is cancelled, ending
    /// any command the system step runs and every process that command started, a dispatch the flow run holds with
    /// no call in progress is stopped, and the flow run's worktree is removed once the worker has returned from its step. It returns without waiting for any of that.
    /// </summary>
    void Stopped(string project, Guid flowRunId);

    /// <summary>
    /// Marks a person's stop of the flow run as being saved, from before it is saved until the mark is
    /// disposed, which the caller does once it has called <see cref="Stopped"/> or the stop was refused.
    /// Until then the module feed holds the flow run's stopped delivery, as it does while a worker advances it.
    /// </summary>
    IDisposable Stopping(Guid flowRunId);
}
