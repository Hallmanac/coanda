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
    public ModelTriple? Model { get; init; }
    public JsonElement? Answer { get; init; }
    public Measurement? Measurement { get; init; }
    public JsonElement? Result { get; init; }
    public DateTimeOffset? RecordedAt { get; init; }

    public bool IsRecorded => RecordedAt is not null;
}
