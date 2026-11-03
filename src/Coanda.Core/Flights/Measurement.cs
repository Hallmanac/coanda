namespace Coanda.Core.Flights;

/// <summary>What one judgment cost: tokens, cost in US dollars, turns, and how long it took.</summary>
public sealed record Measurement(long Tokens, decimal CostUsd, int Turns, TimeSpan Duration);
