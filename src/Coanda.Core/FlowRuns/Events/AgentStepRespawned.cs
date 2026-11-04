namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// A resumed agent step whose visit paused without an answer is performed again on the same
/// visit, so <paramref name="Agent"/> is spawned once more: one more spawn for the flow run, and no
/// visit, since the step was not entered again.
/// </summary>
public sealed record AgentStepRespawned(
    Guid FlowRunId,
    string Step,
    string Agent,
    DateTimeOffset RespawnedAt);
