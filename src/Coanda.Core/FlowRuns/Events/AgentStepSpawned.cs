using Coanda.Core.Agents;

namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// The flow run's current agent step launched <paramref name="Agent"/>, using the spawn the visit
/// last counted, on the agent definition whose content hash is <paramref name="AgentHash"/> and the
/// model spec <paramref name="Model"/>. It counts nothing itself: a visit's spawn is counted when
/// the step is entered, and a resume's when <see cref="AgentStepRespawned"/> is recorded. The launch
/// performs the resume the visit held, if any. <paramref name="LaunchId"/> is the value the daemon
/// assigned this launch, which tells it apart from every other launch of the visit. A launch
/// recorded before the hash and the model spec were carried replays with both null, and one
/// recorded before launches carried an id replays with none.
/// </summary>
public sealed record AgentStepSpawned(
    Guid FlowRunId,
    string Step,
    string Agent,
    string? AgentHash,
    ModelSpec? Model,
    DateTimeOffset SpawnedAt,
    Guid? LaunchId = null);
