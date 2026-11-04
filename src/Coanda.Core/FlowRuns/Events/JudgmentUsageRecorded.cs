namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// A judgment on step <paramref name="Step"/> ended without its answer or its pause being recorded,
/// because a person paused or stopped the flow run while it ran or because its visit no longer expected
/// the session <paramref name="SessionId"/>. What it reported spending is kept all the same: the
/// <paramref name="Usage"/> counts toward the flow run's cost as an answer's measurement does. It
/// changes nothing else on the flow run.
/// </summary>
public sealed record JudgmentUsageRecorded(
    Guid FlowRunId,
    string Step,
    string Agent,
    Guid SessionId,
    Measurement Usage,
    DateTimeOffset RecordedAt);
