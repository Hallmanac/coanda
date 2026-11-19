using Coanda.Core.Flows;
using Coanda.Daemon.FlowRuns;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Pauses the flow run until the external event its <c>event</c> argument names, and once the flow run is
/// resumed on this visit the step answers done. A matching event is one this node records after the step's
/// own pause, whose kind equals the <c>event</c> argument and whose parcel id names the flow run's parcel: the
/// trigger loop resumes the flow run for it, where the parcel's claims let this node start a flow run on the
/// parcel (<see cref="Triggers.TriggerLoop"/>). An event of another kind, one naming no parcel or another, one
/// recorded before the pause, and one arriving while the flow run is paused for a reason the step did not
/// answer resume nothing. A person's resume still stands in for the event.
/// </summary>
public sealed class WaitForExternalEventSystemStep : IFlowSystemStep
{
    public const string EventArgument = "event";

    public string Name => CoreSystemSteps.WaitForExternalEvent;

    public ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        string awaited = SystemStepArguments.RequireString(request.Step, EventArgument);
        SystemStepAnswer outcome = request.Resumed
            ? SystemStepAnswer.DoneWith(Name, (EventArgument, awaited))
            : new SystemStepAnswer.Paused(
                $"Step '{request.Step.Name}' waits for the external event '{awaited}'.",
                FlowRunLevers.PerformAgain(request.Step.Name));
        return ValueTask.FromResult(outcome);
    }
}
