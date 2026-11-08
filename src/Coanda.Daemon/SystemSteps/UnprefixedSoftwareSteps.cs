using System.Text.Json;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Sdk.Contract;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// How the daemon reads a flow version whose content names one of the five software steps (worktree,
/// gates, open-pr, gated-merge, rebase) without the software module's prefix, as every flow did before
/// the steps were carved into a module. This reads definition content, not an earlier record: such a
/// flow version keeps its content and its content hash, since the flow runs under it recorded that hash
/// and a flow run keeps the flow it started under, so it is a current shape and this mapping is
/// permanent. Its steps are dispatched, asked their status, stopped, checked, and recorded under their
/// <c>software/</c> names (<see cref="CurrentName"/>, <see cref="Current{TEvent}"/>), and its open-pr step,
/// when it routes on <c>done</c> alone, still advances when open-pr answers <c>opened</c>
/// (<see cref="OpenPrResult"/>).
/// </summary>
public static class UnprefixedSoftwareSteps
{
    /// <summary>The module the five steps belong to.</summary>
    public const string Module = "software";

    /// <summary>The five system steps a flow from before the carve-out names without the software module's prefix.</summary>
    public static readonly IReadOnlyList<string> Names = ["worktree", "gates", "open-pr", "gated-merge", "rebase"];

    private const string UnprefixedOpenPr = "open-pr";
    private const string OpenedVerdict = "opened";

    /// <summary>
    /// The prefixed name of <paramref name="systemStep"/> when it is one of the five <see cref="Names"/>, and
    /// <paramref name="systemStep"/> as it is otherwise.
    /// </summary>
    public static string CurrentName(string systemStep) =>
        Names.Contains(systemStep, StringComparer.Ordinal)
            ? $"{Module}/{systemStep}"
            : systemStep;

    /// <summary>
    /// <paramref name="recorded"/>, an event a flow run is about to append, with any of the five steps it
    /// names as a system step named prefixed, as a flow run under such a flow version decides it. Every other
    /// event is returned as it is.
    /// </summary>
    public static TEvent Current<TEvent>(TEvent recorded)
        where TEvent : notnull =>
        recorded switch
        {
            FlowRunStarted { FirstStepKind: StepKind.SystemStep } started => (TEvent)(object)(started with { FirstStepPerformer = CurrentName(started.FirstStepPerformer) }),
            StepStarted { Kind: StepKind.SystemStep } started => (TEvent)(object)(started with { Performer = CurrentName(started.Performer) }),
            SystemStepDispatched dispatched => (TEvent)(object)(dispatched with { SystemStep = CurrentName(dispatched.SystemStep) }),
            SystemStepCompleted completed => (TEvent)(object)(completed with { SystemStep = CurrentName(completed.SystemStep) }),
            _ => recorded,
        };

    /// <summary>
    /// The result a flow run records for <paramref name="result"/>, which a module answered for the flow step
    /// <paramref name="step"/>. A flow written before the build flow ended at the open pull request names
    /// open-pr unprefixed and routes it on <c>done</c> alone, which open-pr answered before it answered
    /// <c>opened</c>; such a step's <c>opened</c> is answered <c>done</c>. A step naming
    /// <c>software/open-pr</c>, or routing on <c>opened</c>, gets the verdict the module answered.
    /// </summary>
    public static StepResult OpenPrResult(FlowStep step, StepResult result) =>
        step.SystemStep == UnprefixedOpenPr
        && result.Verdict == OpenedVerdict
        && RoutesOn(step, SystemStepAnswer.DoneVerdict)
        && !RoutesOn(step, OpenedVerdict)
            ? result with { Verdict = SystemStepAnswer.DoneVerdict }
            : result;

    private static bool RoutesOn(FlowStep step, string verdict) =>
        step.Transitions.Any(t =>
            t.Field == SystemStepAnswer.Verdict
            && t.Value.ValueKind == JsonValueKind.String
            && t.Value.GetString() == verdict);
}
