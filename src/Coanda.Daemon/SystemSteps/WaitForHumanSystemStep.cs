using Coanda.Core.Flows;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Pauses the flow run for a person, with the step's <c>lever</c> argument as the lever. A resume is
/// what it waits for, so once the flow run is resumed on this visit the step answers done.
/// </summary>
public sealed class WaitForHumanSystemStep : IFlowSystemStep
{
    public const string LeverArgument = "lever";

    public string Name => CoreSystemSteps.WaitForHuman;

    public ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        string lever = SystemStepArguments.RequireString(request.Step, LeverArgument);
        SystemStepAnswer outcome = request.Resumed
            ? SystemStepAnswer.DoneWith(Name)
            : new SystemStepAnswer.Paused($"Step '{request.Step.Name}' waits for a person.", lever);
        return ValueTask.FromResult(outcome);
    }
}
