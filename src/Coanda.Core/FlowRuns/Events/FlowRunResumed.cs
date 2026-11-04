namespace Coanda.Core.FlowRuns.Events;

public sealed record FlowRunResumed(
    Guid FlowRunId,
    string ResumedBy,
    string Note,
    DateTimeOffset ResumedAt);
