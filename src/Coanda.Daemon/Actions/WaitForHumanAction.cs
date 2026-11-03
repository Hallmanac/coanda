using Coanda.Core.Flows;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Parks the flight for a person, with the step's <c>lever</c> argument as the lever. A resume is
/// what it waits for, so once the flight is resumed on this visit the step answers done.
/// </summary>
public sealed class WaitForHumanAction : IFlowAction
{
    public const string LeverArgument = "lever";

    public string Name => CoreActions.WaitForHuman;

    public ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        string lever = ActionArguments.RequireString(request.Step, LeverArgument);
        ActionOutcome outcome = request.Resumed
            ? ActionOutcome.DoneWith(Name)
            : new ActionOutcome.Parked($"Step '{request.Step.Name}' waits for a person.", lever);
        return ValueTask.FromResult(outcome);
    }
}
