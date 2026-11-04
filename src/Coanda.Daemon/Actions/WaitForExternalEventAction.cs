using Coanda.Core.Flows;
using Coanda.Daemon.Flights;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Pauses the flight until the event its <c>event</c> argument names. Phase 1 has no event
/// subscriptions, so a person resuming the flight stands in for the event, and once the flight is
/// resumed on this visit the step answers done.
/// </summary>
public sealed class WaitForExternalEventAction : IFlowAction
{
    public const string EventArgument = "event";

    public string Name => CoreActions.WaitForExternalEvent;

    public ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        string awaited = ActionArguments.RequireString(request.Step, EventArgument);
        ActionOutcome outcome = request.Resumed
            ? ActionOutcome.DoneWith(Name, (EventArgument, awaited))
            : new ActionOutcome.Paused(
                $"Step '{request.Step.Name}' waits for the external event '{awaited}'.",
                FlightLevers.PerformAgain(request.Step.Name));
        return ValueTask.FromResult(outcome);
    }
}
