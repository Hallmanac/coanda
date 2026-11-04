namespace Coanda.Core.FlowRuns;

/// <summary>
/// Where a flow run stands in its life. A running or paused flow run is live; an ended or stopped one
/// is not, and its parcel is free for another.
/// </summary>
public enum FlowRunStatus
{
    Running,
    Paused,
    Ended,
    Stopped,
}
