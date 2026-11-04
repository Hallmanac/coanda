namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// The executor returned the session <paramref name="SessionId"/> for the launch the flow run's current
/// agent step last recorded, before the session was first collected. It counts nothing.
/// </summary>
public sealed record AgentStepSessionOpened(
    Guid FlowRunId,
    string Step,
    Guid SessionId,
    DateTimeOffset OpenedAt);
