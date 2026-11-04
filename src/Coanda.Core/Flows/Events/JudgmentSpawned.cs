namespace Coanda.Core.Flows.Events;

/// <summary>
/// The flow's current judgment step launched <paramref name="Agent"/>, using the spawn the visit
/// last counted. It counts nothing itself: a visit's spawn is counted when the step is entered, and
/// a resume's when <see cref="JudgmentRespawned"/> is recorded. The launch performs the resume the
/// visit held, if any.
/// </summary>
public sealed record JudgmentSpawned(
    Guid FlowId,
    string Step,
    string Agent,
    DateTimeOffset SpawnedAt);
