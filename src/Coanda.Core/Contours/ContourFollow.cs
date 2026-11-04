namespace Coanda.Core.Contours;

/// <summary>
/// What a contour follows: another flow run's typed outcome on the same parcel. A flow run ending with
/// <see cref="Outcome"/>, of the contour named <see cref="Contour"/> when one is named, starts this contour on
/// that flow run's parcel as the next link of its chain. <see cref="DepthCap"/> is the deepest link
/// that performs a step. A follow is an equality match and never an expression.
/// </summary>
public sealed record ContourFollow
{
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The contour that must have produced the outcome, or null when any contour's will do.</summary>
    public string? Contour { get; init; }

    /// <summary>The greatest depth at which a flow run of the following contour performs a step; the root of a chain is at depth zero.</summary>
    public int DepthCap { get; init; }
}
