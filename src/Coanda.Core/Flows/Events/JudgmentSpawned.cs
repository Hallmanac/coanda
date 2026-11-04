using Coanda.Core.Agents;

namespace Coanda.Core.Flows.Events;

/// <summary>
/// The flow's current judgment step launched <paramref name="Agent"/>, using the spawn the visit
/// last counted, on the agent definition whose content hash is <paramref name="AgentHash"/> and the
/// model spec <paramref name="Model"/>. It counts nothing itself: a visit's spawn is counted when
/// the step is entered, and a resume's when <see cref="JudgmentRespawned"/> is recorded. The launch
/// performs the resume the visit held, if any. A launch recorded before the hash and the model spec
/// were carried replays with both null.
/// </summary>
public sealed record JudgmentSpawned(
    Guid FlowId,
    string Step,
    string Agent,
    string? AgentHash,
    ModelSpec? Model,
    DateTimeOffset SpawnedAt);
