namespace Coanda.Core.FlowRuns.Events;

/// <summary>A flow run reached the end of its flow and carries the outcome the flow declares.</summary>
public sealed record FlowRunEnded(
    Guid FlowRunId,
    string Outcome,
    string EndedBy,
    DateTimeOffset EndedAt);
