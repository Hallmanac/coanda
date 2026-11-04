using System.Text.Json;

namespace Coanda.Core.Contours;

/// <summary>
/// One row of a step's routing table: when the last answer's <see cref="Field"/> equals
/// <see cref="Value"/> and the step has been visited no more than <see cref="VisitCap"/> times, go to
/// <see cref="Target"/>, which is a step of the contour, <see cref="Contour.Pause"/>, or
/// <see cref="Contour.End"/>. A property that is missing reads as blank, zero, or undefined,
/// so validation can name the rule rather than the parser failing first.
/// </summary>
public sealed record ContourTransition
{
    public string Field { get; init; } = string.Empty;
    public JsonElement Value { get; init; }
    public int VisitCap { get; init; }
    public string Target { get; init; } = string.Empty;
}
