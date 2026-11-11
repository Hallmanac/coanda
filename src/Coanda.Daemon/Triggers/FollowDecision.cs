namespace Coanda.Daemon.Triggers;

/// <summary>
/// The trigger loop's decision on one follower of a flow run's end: the follower's flow, the ended
/// flow run, and whether the follower waits on the parcel's deferred follows rather than starting now.
/// </summary>
internal sealed record FollowDecision(string Project, Guid ParentFlowRunId, string Flow, bool Waits);
