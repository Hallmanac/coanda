namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// A person resumed a paused flow run. <see cref="Answer"/> is the trimmed answer the person gave for
/// the step the resume landed on, or null when the resume gave none; a resumed event recorded before
/// resumes carried answers reads with none.
/// </summary>
public sealed record FlowRunResumed(
    Guid FlowRunId,
    string ResumedBy,
    string Note,
    DateTimeOffset ResumedAt,
    string? Answer = null);
