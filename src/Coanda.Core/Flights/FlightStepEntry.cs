using System.Text.Json;
using Coanda.Core.Agents;

namespace Coanda.Core.Flights;

/// <summary>
/// One step a flight entered, in the order entered, with what was recorded on that visit: the
/// answer and its measurement for a judgment, the result for an action. Null until recorded.
/// </summary>
public sealed record FlightStepEntry
{
    public string Step { get; init; } = string.Empty;
    public StepKind Kind { get; init; }

    /// <summary>The agent a judgment step names, or the action an action step names.</summary>
    public string Performer { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }
    public string? AgentHash { get; init; }
    public ModelSpec? Model { get; init; }
    public JsonElement? Answer { get; init; }
    public Measurement? Measurement { get; init; }
    public JsonElement? Result { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }

    /// <summary>
    /// When the flight was resumed while this visit was its current step, or null when it was not,
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
}
