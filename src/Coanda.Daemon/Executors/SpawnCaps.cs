namespace Coanda.Daemon.Executors;

/// <summary>
/// The limits one spawn runs under: a cost ceiling in US dollars and a wall-clock limit. The
/// caller derives them; an adapter passes them to the vendor where it can, and the daemon's own
/// timer stays authoritative.
/// </summary>
public sealed record SpawnCaps(decimal CostCeilingUsd, TimeSpan WallClockLimit);
