using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coanda.Core.Flows;

/// <summary>
/// One row of a step's routing table: when the last answer's <see cref="Field"/> equals
/// <see cref="Value"/> and the step has been visited no more than <see cref="VisitCap"/> times, go to
/// <see cref="Target"/>, which is a step of the flow, <see cref="Flow.Pause"/>, or
/// <see cref="Flow.End"/>. A transition to end may name the <see cref="Outcome"/> the flow run ends
/// with; one naming none ends with the flow's declared outcome. A property that is missing reads as
/// blank, zero, or undefined, so validation can name the rule rather than the parser failing first.
/// </summary>
public sealed record FlowTransition
{
    public string Field { get; init; } = string.Empty;
    public JsonElement Value { get; init; }
    public int VisitCap { get; init; }
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// The outcome a flow run ends with when it ends through this transition, or null when it ends
    /// with its flow's declared outcome. Only a transition whose target is end names one. Left out of
    /// the content when null, so a transition without one reads and hashes as it did before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Outcome { get; init; }
}
