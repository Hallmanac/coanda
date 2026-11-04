namespace Coanda.Core.Flows;

/// <summary>
/// What a flow follows: another flow run's typed outcome on the same parcel. A flow run ending with
/// <see cref="Outcome"/>, of the flow named <see cref="Flow"/> when one is named, starts this flow on
/// that flow run's parcel as the next link of its chain. <see cref="DepthCap"/> is the deepest link
/// that performs a step. A follow is an equality match and never an expression.
/// </summary>
public sealed record FlowFollow
{
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The flow that must have produced the outcome, or null when any flow's will do.</summary>
    public string? Flow { get; init; }

    /// <summary>The greatest depth at which a flow run of the following flow performs a step; the root of a chain is at depth zero.</summary>
    public int DepthCap { get; init; }
}
