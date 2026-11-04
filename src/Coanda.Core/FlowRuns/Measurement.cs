namespace Coanda.Core.FlowRuns;

/// <summary>What one agent step cost: tokens, cost in US dollars, turns, and how long it took.</summary>
public sealed record Measurement(long Tokens, decimal CostUsd, int Turns, TimeSpan Duration);
