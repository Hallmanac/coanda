namespace Coanda.Daemon.Flows;

/// <summary>
/// The daemon's requests of the worker that advances a flow by its id. The start endpoint, a
/// resume, and the daemon's own start ask for an advance; a person's pause or stop asks for the
/// judgment in progress to end. <see cref="FlowRunner"/> answers both.
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
}
