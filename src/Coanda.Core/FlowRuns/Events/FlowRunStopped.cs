namespace Coanda.Core.FlowRuns.Events;

/// <summary>Someone stopped a live flow run before its end, so its parcel is free for another.</summary>
public sealed record FlowRunStopped(
    Guid FlowRunId,
    string StoppedBy,
    string Reason,
    DateTimeOffset StoppedAt);
