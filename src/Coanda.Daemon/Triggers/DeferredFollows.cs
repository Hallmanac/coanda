namespace Coanda.Daemon.Triggers;

/// <summary>
/// The follows the trigger loop could not start yet because their parcel already had a live flow,
/// most often another follower of the same end. One document per parcel in the parcel's project, so
/// it survives a restart; each release of the parcel's live flow starts the oldest pending follow,
/// and a follow still waiting keeps its place until a later release.
/// </summary>
public sealed class DeferredFollows
{
    /// <summary>The parcel's id: every follow waiting on a parcel waits in its one document.</summary>
    public Guid Id { get; set; }

    /// <summary>The follows waiting for the parcel, oldest first.</summary>
    public List<PendingFollow> Pending { get; set; } = [];
}

/// <summary>A follow waiting to start: the ended flow it follows and the contour that follows it.</summary>
public sealed record PendingFollow(Guid ParentFlowId, string Contour);
