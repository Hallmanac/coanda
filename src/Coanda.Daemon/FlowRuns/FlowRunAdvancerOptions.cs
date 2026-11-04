namespace Coanda.Daemon.FlowRuns;

/// <summary>What the runner is given when the daemon starts: the wall-clock limit on one agent step, its retry included.</summary>
public sealed record FlowRunAdvancerOptions(TimeSpan AgentStepLimit);
