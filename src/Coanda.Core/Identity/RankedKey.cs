namespace Coanda.Core.Identity;

/// <summary>A live key of the ranked root set: the key, its rank (0 is the original root, and a lower rank outranks a higher one), and the node it signs for.</summary>
public sealed record RankedKey(string Key, int Rank, Guid NodeId);
