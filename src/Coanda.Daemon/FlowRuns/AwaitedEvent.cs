using System.Text.Json;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Triggers;
using Coanda.Daemon.SystemSteps;
using JasperFx.Events;

namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// Whether an external event is the one a flow run's wait-for-external-event step waits for, so the trigger loop
/// resumes the flow run for it. It is when every one of these holds of the flow run, as its stream stands: it is
/// paused; its current visit is to a wait-for-external-event step whose <c>event</c> argument equals the event's
/// kind, ordinally, and has recorded nothing; the latest pause in its stream is that visit's own, the outcome of its
/// latest dispatch, and not a person's pause or a cap's; that pause comes before the event in this node's record,
/// compared by global sequence; and no resume in its stream names the event's resumer already. Matching the event's
/// parcel to the flow run's is the caller's, which finds the flow run as the parcel's live one.
/// </summary>
public static class AwaitedEvent
{
    public static bool Matches(
        Flow flow, FlowRun flowRun, IReadOnlyList<IEvent> stream, string kind, long sequence, string resumedBy)
    {
        if (flowRun.Status != FlowRunStatus.Paused
            || flowRun.Current is not { Kind: StepKind.SystemStep, IsRecorded: false, DispatchId: Guid dispatchId, DispatchOutcomeAt: not null } current
            || flow.Step(current.Step) is not { SystemStep: CoreSystemSteps.WaitForExternalEvent } step
            || !Awaits(step, kind))
        {
            return false;
        }

        IEvent? pause = stream.LastOrDefault(e => e.Data is FlowRunPaused);
        return pause is { Data: FlowRunPaused { DispatchId: Guid paused } }
            && paused == dispatchId
            && pause.Sequence < sequence
            && !stream.Any(e => e.Data is FlowRunResumed resumed && string.Equals(resumed.ResumedBy, resumedBy, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether <paramref name="paused"/> is a wait-for-external-event step's own pause, the one an external event can
    /// resume: <paramref name="atPause"/>, the flow run as its stream stood at the pause, is on a visit to such a step;
    /// the pause is the outcome of that visit's dispatch; and the step's <c>event</c> argument is a kind an external
    /// event may be recorded under. A pause the step's refusal of its own arguments answered, a dispatch failure, a
    /// person's pause, and a pause waiting on a kind no external event may use are none of these: nothing but a person
    /// clears them.
    /// </summary>
    public static bool IsWaitPause(Flow flow, FlowRun atPause, FlowRunPaused paused) =>
        atPause.Current is { Kind: StepKind.SystemStep, DispatchId: Guid dispatchId } current
        && paused.DispatchId == dispatchId
        && flow.Step(current.Step) is { SystemStep: CoreSystemSteps.WaitForExternalEvent } step
        && step.Arguments.TryGetValue(WaitForExternalEventSystemStep.EventArgument, out JsonElement awaited)
        && awaited.ValueKind == JsonValueKind.String
        && awaited.GetString() is { } kind
        && !string.IsNullOrWhiteSpace(kind)
        && TriggerDecider.MayBeRecorded(kind);

    private static bool Awaits(FlowStep step, string kind) =>
        step.Arguments.TryGetValue(WaitForExternalEventSystemStep.EventArgument, out JsonElement awaited)
        && awaited.ValueKind == JsonValueKind.String
        && string.Equals(awaited.GetString(), kind, StringComparison.Ordinal);
}
