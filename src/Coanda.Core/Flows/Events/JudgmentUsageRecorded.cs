namespace Coanda.Core.Flows.Events;

/// <summary>
/// A judgment on step <paramref name="Step"/> ended without its answer or its pause being recorded,
/// because a person paused or stopped the flow while it ran or because its visit no longer expected
/// the session <paramref name="SessionId"/>. What it reported spending is kept all the same: the
/// <paramref name="Usage"/> counts toward the flow's cost as an answer's measurement does. It
/// changes nothing else on the flow.
/// </summary>
public sealed record JudgmentUsageRecorded(
    Guid FlowId,
    string Step,
    string Agent,
    Guid SessionId,
    Measurement Usage,
    DateTimeOffset RecordedAt);
