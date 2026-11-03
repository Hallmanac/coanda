namespace Coanda.Daemon.Flights;

/// <summary>What the runner is given when the daemon starts: the wall-clock limit on one judgment, its retry included.</summary>
public sealed record FlightRunnerOptions(TimeSpan JudgmentLimit);
