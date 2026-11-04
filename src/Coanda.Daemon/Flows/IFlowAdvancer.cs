namespace Coanda.Daemon.Flows;

/// <summary>
/// The daemon's requests of the worker that advances a flow by its id. The start endpoint, a
/// resume, and the daemon's own start ask for an advance; a person's pause asks for the judgment in
/// progress to end, and a person's stop for the judgment or action in progress to end and the
/// flow's worktree to be removed. <see cref="FlowRunner"/> answers all three.
/// </summary>
public interface IFlowAdvancer
{
    /// <summary>
    /// Asks for the flow to be advanced until it ends, pauses, or stops. A request for a flow
    /// already being advanced joins that work rather than starting a second worker. The task
    /// completes when no worker is advancing the flow any more.
    /// </summary>
    Task RequestAdvance(string project, Guid flowId);

    /// <summary>
    /// Tells the worker advancing the flow that a person's pause or stop of it was saved, so the
    /// judgment it is running ends now, its session stopped through the executor, rather than when
    /// the agent answers or the wall-clock limit passes. It returns without waiting for that, and
    /// does nothing when no judgment of the flow is running.
    /// </summary>
    void EndJudgment(Guid flowId);

    /// <summary>
    /// Tells the worker advancing the flow that a person's stop of it was saved: the judgment it is
    /// running ends as for <see cref="EndJudgment"/>, the action it is performing is cancelled, ending
    /// any command the action runs and every process that command started, and the flow's worktree is
    /// removed once the worker has returned from its step. It returns without waiting for any of that.
    /// </summary>
    void Stopped(string project, Guid flowId);
}
