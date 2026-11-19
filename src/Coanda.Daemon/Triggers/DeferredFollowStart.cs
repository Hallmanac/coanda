namespace Coanda.Daemon.Triggers;

/// <summary>
/// A follow from a parcel's follow queue that the trigger loop is about to start, the parcel being free:
/// the parcel, the ended flow run it follows, and the flow that follows it.
/// </summary>
internal sealed record DeferredFollowStart(string Project, Guid ParcelId, Guid ParentFlowRunId, string Flow);
