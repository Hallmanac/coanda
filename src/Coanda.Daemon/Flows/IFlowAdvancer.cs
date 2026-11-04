namespace Coanda.Daemon.Flows;

/// <summary>
/// The daemon's one request to advance a flow by its id. The start endpoint, a resume, and the
/// daemon's own start all make it; <see cref="FlowRunner"/> answers it.
/// </summary>
public interface IFlowAdvancer
{
    /// <summary>
    /// Asks for the flow to be advanced until it ends, pauses, or stops. A request for a flow
    /// already being advanced joins that work rather than starting a second worker. The task
    /// completes when no worker is advancing the flow any more.
    /// </summary>
    Task RequestAdvance(string project, Guid flowId);
}
