namespace Coanda.Core.Flows.Events;

/// <summary>
/// A resumed judgment step whose visit paused without an answer is performed again on the same
/// visit, so <paramref name="Agent"/> is spawned once more: one more spawn for the flow, and no
/// visit, since the step was not entered again.
/// </summary>
public sealed record JudgmentRespawned(
    Guid FlowId,
    string Step,
    string Agent,
    DateTimeOffset RespawnedAt);
