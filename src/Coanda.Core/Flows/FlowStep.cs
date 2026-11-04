using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coanda.Core.Flows;

/// <summary>
/// A step of a flow. An agent step names an <see cref="Agent"/> and an <see cref="AnswerSchema"/>; a
/// system step names a <see cref="SystemStep"/> and holds zero or more named <see cref="Arguments"/>. Exactly
/// one of agent and system step is set. An agent step may declare the parcel <see cref="Fields"/> its context
/// carries, and the <see cref="Sources"/> whose latest recordings on the same flow run it carries after them;
/// a system step declares neither.
/// </summary>
public sealed record FlowStep
{
    public string Name { get; init; } = string.Empty;
    public string? Agent { get; init; }
    public string? AnswerSchema { get; init; }
    public string? SystemStep { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Arguments { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyList<FlowTransition> Transitions { get; init; } = [];

    /// <summary>The parcel fields an agent step's context carries, in this order; none for a system step.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];

    /// <summary>The sources an agent step's context carries after its fields, in this order; none for a system step.</summary>
    public IReadOnlyList<FlowSource> Sources { get; init; } = [];

    /// <summary>Whether the step is an agent step; derived from <see cref="Agent"/>, so never written into content.</summary>
    [JsonIgnore]
    public bool IsAgentStep => !string.IsNullOrWhiteSpace(Agent);

    /// <summary>Whether the step is a system step; derived from <see cref="SystemStep"/>, so never written into content.</summary>
    [JsonIgnore]
    public bool IsSystemStep => !string.IsNullOrWhiteSpace(SystemStep);
}
