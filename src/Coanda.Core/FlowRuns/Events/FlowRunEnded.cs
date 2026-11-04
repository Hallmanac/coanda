namespace Coanda.Core.FlowRuns.Events;

/// <summary>A flow run reached the end of its contour and carries the outcome the contour declares.</summary>
public sealed record FlowRunEnded(
    Guid FlowRunId,
    string Outcome,
    string EndedBy,
    DateTimeOffset EndedAt);
