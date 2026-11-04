namespace Coanda.Core.Flows.Events;

/// <summary>
/// The executor returned the session <paramref name="SessionId"/> for the launch the flow's current
/// judgment step last recorded, before the session was first collected. It counts nothing.
/// </summary>
public sealed record JudgmentSessionOpened(
    Guid FlowId,
    string Step,
    Guid SessionId,
    DateTimeOffset OpenedAt);
