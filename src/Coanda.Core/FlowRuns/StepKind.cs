namespace Coanda.Core.FlowRuns;

/// <summary>What a flow step is: a judgment an agent answers, or an action only the daemon does.</summary>
public enum StepKind
{
    Judgment,
    Action,
}
