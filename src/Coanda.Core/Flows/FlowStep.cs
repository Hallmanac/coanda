using System.Text.Json;

namespace Coanda.Core.Flows;

/// <summary>
/// A step of a flow. A judgment names an <see cref="Agent"/> and an <see cref="AnswerSchema"/>; an
/// action names an <see cref="Action"/> and holds zero or more named <see cref="Arguments"/>. Exactly
/// one of agent and action is set.
/// </summary>
public sealed record FlowStep
{
    public string Name { get; init; } = string.Empty;
    public string? Agent { get; init; }
    public string? AnswerSchema { get; init; }
    public string? Action { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<FlowTransition> Transitions { get; init; } = [];

    public bool IsJudgment => !string.IsNullOrWhiteSpace(Agent);

    public bool IsAction => !string.IsNullOrWhiteSpace(Action);
}
