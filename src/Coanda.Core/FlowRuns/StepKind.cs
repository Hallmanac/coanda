namespace Coanda.Core.FlowRuns;

/// <summary>What a flow step is: an agent step an agent answers, or a system step only the daemon does.</summary>
public enum StepKind
{
    AgentStep,
    SystemStep,
}
