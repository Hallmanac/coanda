using Coanda.Core.Flows;
using Coanda.Daemon.FlowRuns;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Pauses the flow run until the event its <c>event</c> argument names. Phase 1 has no event
/// subscriptions, so a person resuming the flow run stands in for the event, and once the flow run is
/// resumed on this visit the step answers done.
/// </summary>
public sealed class WaitForExternalEventSystemStep : IFlowSystemStep
{
    public const string EventArgument = "event";

    public string Name => CoreSystemSteps.WaitForExternalEvent;

    public ValueTask<SystemStepOutcome> PerformAsync(SystemStepRequest request, CancellationToken cancellationToken)
    {
        string awaited = SystemStepArguments.RequireString(request.Step, EventArgument);
        SystemStepOutcome outcome = request.Resumed
            ? SystemStepOutcome.DoneWith(Name, (EventArgument, awaited))
            : new SystemStepOutcome.Paused(
                $"Step '{request.Step.Name}' waits for the external event '{awaited}'.",
                FlowRunLevers.PerformAgain(request.Step.Name));
        return ValueTask.FromResult(outcome);
    }
}
