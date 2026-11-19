using System.Text.Json.Serialization;

namespace Coanda.Core.Flows;

/// <summary>
/// One source an agent step's context carries, naming exactly one kind:
/// <list type="bullet">
/// <item>a step of the same flow, <c>{"Step": "&lt;step&gt;"}</c>, whose latest recorded answer or
/// result on the same flow run the context carries;</item>
/// <item>a step of a named flow, <c>{"Flow": "&lt;flow&gt;", "Step": "&lt;step&gt;"}</c>, whose latest
/// recording on the same parcel the context carries; the flow may be the declaring step's own and
/// the step may be the declaring step itself, so a re-review reads its own flow's earlier answer;</item>
/// <item>the open parcels of a named parcel type, <c>{"Type": "&lt;type&gt;"}</c>;</item>
/// <item>the parcel's origin, <c>{"Origin": true}</c>: the parcel it was derived from and the flow run
/// that caused it.</item>
/// </list>
/// Each kind's property is nullable and left out of the content when unset, so a step source writes
/// and hashes as it did before the other kinds existed. Content naming no kind, or more than one, is
/// read and then refused by validation with the source's position rather than by the reader. A named
/// flow or type is checked only as a plain name: an absent one reads as empty in the context.
/// </summary>
public sealed record FlowSource
{
    /// <summary>The flow whose <see cref="Step"/> this source reads on the same parcel, or null for a step of the same flow run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Flow { get; init; }

    /// <summary>The step this source reads: of the same flow, or of <see cref="Flow"/> when that is set; null when the source names none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Step { get; init; }

    /// <summary>The parcel type whose open parcels this source reads, or null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>True when this source reads the parcel's origin; null otherwise. False names no kind and is refused.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Origin { get; init; }
}
