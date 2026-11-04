namespace Coanda.Core.Flows.Events;

public sealed record FlowResumed(
    Guid FlowId,
    string ResumedBy,
    string Note,
    DateTimeOffset ResumedAt);
