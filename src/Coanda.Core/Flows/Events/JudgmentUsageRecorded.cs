namespace Coanda.Core.Flows.Events;

/// <summary>
/// A judgment on step <paramref name="Step"/> finished after its flow stopped running (paused or
/// stopped while the agent worked), so its answer was not recorded, but what it reported spending
/// was: the <paramref name="Usage"/> counts against the budget as an answer's cost does.
/// </summary>
public sealed record JudgmentUsageRecorded(
    Guid FlowId,
    string Step,
    string Agent,
    Measurement Usage,
    DateTimeOffset RecordedAt);
