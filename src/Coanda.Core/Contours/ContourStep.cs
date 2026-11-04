using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coanda.Core.Contours;

/// <summary>
/// A step of a contour. A judgment names an <see cref="Agent"/> and an <see cref="AnswerSchema"/>; an
/// action names an <see cref="Action"/> and holds zero or more named <see cref="Arguments"/>. Exactly
/// one of agent and action is set.
/// </summary>
public sealed record ContourStep
{
    public string Name { get; init; } = string.Empty;
    public string? Agent { get; init; }
    public string? AnswerSchema { get; init; }
    public string? Action { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<ContourTransition> Transitions { get; init; } = [];

    /// <summary>Whether the step is a judgment; derived from <see cref="Agent"/>, so never written into content.</summary>
    [JsonIgnore]
    public bool IsJudgment => !string.IsNullOrWhiteSpace(Agent);

    /// <summary>Whether the step is an action; derived from <see cref="Action"/>, so never written into content.</summary>
    [JsonIgnore]
    public bool IsAction => !string.IsNullOrWhiteSpace(Action);
}
