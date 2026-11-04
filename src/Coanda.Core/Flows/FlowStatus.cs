namespace Coanda.Core.Flows;

/// <summary>
/// Where a flow stands in its life. A running or paused flow is live; an ended or stopped one
/// is not, and its parcel is free for another.
/// </summary>
public enum FlowStatus
{
    Running,
    Paused,
    Ended,
    Stopped,
}
