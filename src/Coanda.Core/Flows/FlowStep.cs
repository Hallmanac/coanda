using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coanda.Core.Flows;

/// <summary>
/// A step of a flow. A judgment names an <see cref="Agent"/> and an <see cref="AnswerSchema"/>; an
/// action names an <see cref="Action"/> and holds zero or more named <see cref="Arguments"/>. Exactly
/// one of agent and action is set. A judgment may declare the parcel <see cref="Fields"/> its context
/// carries, and the <see cref="Sources"/> whose latest recordings on the same flow run it carries after them;
/// an action declares neither.
/// </summary>
public sealed record FlowStep
{
    public string Name { get; init; } = string.Empty;
    public string? Agent { get; init; }
    public string? AnswerSchema { get; init; }
    public string? Action { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<FlowTransition> Transitions { get; init; } = [];

    /// <summary>The parcel fields a judgment's context carries, in this order; none for an action.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>The sources a judgment's context carries after its fields, in this order; none for an action.</summary>
    public IReadOnlyList<FlowSource> Sources { get; init; } = [];

    /// <summary>Whether the step is a judgment; derived from <see cref="Agent"/>, so never written into content.</summary>
    [JsonIgnore]
    public bool IsJudgment => !string.IsNullOrWhiteSpace(Agent);

    /// <summary>Whether the step is an action; derived from <see cref="Action"/>, so never written into content.</summary>
    [JsonIgnore]
    public bool IsAction => !string.IsNullOrWhiteSpace(Action);
}
