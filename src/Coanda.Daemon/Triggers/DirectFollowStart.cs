namespace Coanda.Daemon.Triggers;

/// <summary>
/// A follower of a flow run's end that the trigger loop is about to start straight from that end, the parcel being
/// free: the parcel, the ended flow run, and the flow that follows it.
/// </summary>
internal sealed record DirectFollowStart(string Project, Guid ParcelId, Guid ParentFlowRunId, string Flow);
