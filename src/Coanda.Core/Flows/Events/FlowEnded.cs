namespace Coanda.Core.Flows.Events;

/// <summary>A flow reached the end of its contour and carries the outcome the contour declares.</summary>
public sealed record FlowEnded(
    Guid FlowId,
    string Outcome,
    string EndedBy,
    DateTimeOffset EndedAt);
