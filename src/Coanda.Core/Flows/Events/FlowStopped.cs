namespace Coanda.Core.Flows.Events;

/// <summary>Someone stopped a live flow before its end, so its parcel is free for another.</summary>
public sealed record FlowStopped(
    Guid FlowId,
    string StoppedBy,
    string Reason,
    DateTimeOffset StoppedAt);
