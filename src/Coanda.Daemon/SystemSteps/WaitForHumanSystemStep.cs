using Coanda.Core.Flows;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Pauses the flow run for a person, with the step's <c>lever</c> argument as the lever. A resume is
/// what it waits for: once the flow run is resumed on this visit the step answers done, carrying the
/// person's answer, when the resume gave one, as the result's <see cref="WaitForHumanAnswer.Field"/>
/// for the flow to route on. The resume's decision has already refused any answer the step's
/// transitions would not route on. When the step's transitions route on an answer and the resume
/// gave none, it pauses again on the same visit, naming the answers it would take, even when a
/// transition on another field, such as <c>verdict</c>, would fire without one; when it would take
/// none at the visit, <c>coa flow-run stop</c> is the way out.
/// </summary>
public sealed class WaitForHumanSystemStep : IFlowSystemStep
{
    public const string LeverArgument = "lever";

    public string Name => CoreSystemSteps.WaitForHuman;

    public ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        string lever = SystemStepArguments.RequireString(request.Step, LeverArgument);
        SystemStepAnswer outcome = request switch
        {
            { Resumed: false } => new SystemStepAnswer.Paused($"Step '{request.Step.Name}' waits for a person.", lever),
            { Answer: string answer } => SystemStepAnswer.DoneWith(Name, (WaitForHumanAnswer.Field, answer)),
            _ when WaitForHumanAnswer.RoutesOnAnswer(request.Step) => new SystemStepAnswer.Paused(AnswerNeeded(request), lever),
            _ => SystemStepAnswer.DoneWith(Name),
        };
        return ValueTask.FromResult(outcome);
    }

    private static string AnswerNeeded(SystemStepDispatch request)
    {
        IReadOnlyList<string> accepted = WaitForHumanAnswer.Accepted(request.Flow, request.Step, request.Visit, request.Spawns);
        return accepted.Count == 0
            ? $"Step '{request.Step.Name}' waits for a person's answer, and none of its transitions on '{WaitForHumanAnswer.Field}' can take one at this visit; stop the flow run with 'coa flow-run stop'."
            : $"Step '{request.Step.Name}' waits for a person's answer; resume with one of: {string.Join(", ", accepted.Select(answer => $"'{answer}'"))}.";
    }
}
