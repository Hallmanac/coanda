using System.Text.Json;
using Coanda.Core.Agents;

namespace Coanda.Core.Flows;

/// <summary>
/// One step a flow entered, in the order entered, with what was recorded on that visit: the
/// answer and its measurement for a judgment, the result for an action. Null until recorded.
/// </summary>
public sealed record FlowStepEntry
{
    public string Step { get; init; } = string.Empty;
    public StepKind Kind { get; init; }

    /// <summary>The agent a judgment step names, or the action an action step names.</summary>
    public string Performer { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The content hash of the agent a judgment visit last launched or recorded its answer from.</summary>
    public string? AgentHash { get; init; }

    /// <summary>The model spec of the agent a judgment visit last launched or recorded its answer from.</summary>
    public ModelSpec? Model { get; init; }

    public JsonElement? Answer { get; init; }
    public Measurement? Measurement { get; init; }
    public JsonElement? Result { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }

    /// <summary>
    /// When the flow was resumed while this visit was its current step, or null when it was not,
    /// when the visit has recorded its answer or result since, or when a judgment visit has launched
    /// its agent since (so a restart that launches it again is not taken for a resume). A resumed visit is performed
    /// again, as a new visit when it already holds a recording; a resume is also the event a wait
    /// step waits for. A recording clears it, so a recorded visit holding it was resumed after it
    /// recorded, which tells a resume apart from a restart that only has to route the recording.
    /// </summary>
    public DateTimeOffset? ResumedAt { get; init; }

    /// <summary>
    /// When the judgment of this unrecorded visit counted one more spawn for the resume that
    /// <see cref="ResumedAt"/> holds, or null when no spawn has been counted for that resume. A
    /// resume clears it, so a restart that performs the same resumed visit again counts no second
    /// spawn for one resume.
    /// </summary>
    public DateTimeOffset? RespawnedAt { get; init; }

    /// <summary>
    /// When this judgment visit launched its agent on the spawn it last counted, or null while that
    /// spawn is unused: the visit has not launched yet, or a resume counted another spawn since it
    /// did. A visit that paused before it launched holds its counted spawn unused, so a resume
    /// performs it again without counting another.
    /// </summary>
    public DateTimeOffset? SpawnedAt { get; init; }

    /// <summary>
    /// The id of the session the executor returned for this judgment visit's latest launch, or null
    /// while that launch has returned none: a launch or a resume's counted spawn clears it, and the
    /// executor's handing back the session sets it.
    /// </summary>
    public Guid? SessionId { get; init; }

    /// <summary>
    /// The id the daemon assigned this judgment visit's latest launch, or null while the visit has
    /// no launch on the spawn it last counted (or the launch predates launch ids). A launch sets it
    /// and a resume's counted spawn clears it.
    /// </summary>
    public Guid? LaunchId { get; init; }

    /// <summary>
    /// Whether a resume of this visit spawns another agent beyond those already counted: a judgment
    /// resumed since its last count whose counted spawn was used, by a launch or by the recorded
    /// answer only a launch gives.
    /// </summary>
    public bool SpawnsAgainOnResume =>
        Kind == StepKind.Judgment
        && ResumedAt is not null
        && RespawnedAt is null
        && (IsRecorded || SpawnedAt is not null);

    public bool IsRecorded => RecordedAt is not null;

    /// <summary>
    /// Whether this judgment visit launched its agent since it was last resumed and has recorded no
    /// answer: the state a daemon restart finds a judgment in that was running when the daemon
    /// stopped. Such a visit is never launched again on the spawn it already used; its session is
    /// reattached, or the flow pauses when it was lost. A visit resumed since its launch holds
    /// <see cref="ResumedAt"/>, so it is not in this state and is performed as a resume.
    /// </summary>
    public bool AwaitsLaunchedSession =>
        Kind == StepKind.Judgment
        && SpawnedAt is not null
        && ResumedAt is null
        && !IsRecorded;

    /// <summary>
    /// Whether this visit expects what <paramref name="sessionId"/> reports: it is the session the
    /// visit's latest launch returned, and the visit has not been resumed since that launch. A
    /// resume performs the step again with a new spawn, so the session it found running is no
    /// longer the visit's even though its id is still held.
    /// </summary>
    public bool Expects(Guid sessionId) => SessionId == sessionId && ResumedAt is null;

    /// <summary>
    /// Whether this visit still expects what its launch <paramref name="launchId"/> records: it is a
    /// judgment, that launch is its latest, it has not been resumed since, and it has recorded no
    /// answer yet. This is the rule an answer is recorded under, stated for the launch rather than
    /// the session, since a launch is known before its session is returned.
    /// </summary>
    public bool ExpectsLaunch(Guid launchId) =>
        Kind == StepKind.Judgment && LaunchId == launchId && ResumedAt is null && !IsRecorded;
}
