using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coanda.Core.Flows;

/// <summary>
/// The rule for a person's answer to a <see cref="CoreSystemSteps.WaitForHuman"/> step. The step
/// records the answer as the <see cref="Field"/> of its result, beside <c>verdict</c> set to
/// <c>done</c> and <c>systemStep</c> naming the step, and the flow routes on it through its
/// transition table. An answer is accepted only when the transition the interpreter would take at the
/// visit, for that result, is a transition on <see cref="Field"/> whose visit cap allows the visit,
/// so an accepted answer never records and then pauses. The check asks the interpreter
/// (<see cref="Interpreter.Considered"/>, <see cref="Interpreter.Allows"/>) rather than copying how it
/// compares, so a transition whose value is a number or a boolean matches no answer.
/// </summary>
public static class WaitForHumanAnswer
{
    /// <summary>The result field a wait-for-human step records a person's answer in.</summary>
    public const string Field = "answer";

    /// <summary>Whether any of <paramref name="step"/>'s transitions routes on <see cref="Field"/>.</summary>
    public static bool RoutesOnAnswer(FlowStep step) => step.Transitions.Any(OnAnswer);

    /// <summary>
    /// Why <paramref name="answer"/> is refused at <paramref name="step"/>'s visit
    /// <paramref name="visits"/>, counting the current visit, or null when it is accepted.
    /// </summary>
    public static string? Refusal(FlowStep step, int visits, string answer)
    {
        if (!RoutesOnAnswer(step))
        {
            return $"Step '{step.Name}' takes no answer: its transitions route on no answer.";
        }

        using JsonDocument result = Result(answer);
        FlowTransition? considered = Interpreter.Considered(step, result.RootElement);
        return considered switch
        {
            null => $"Step '{step.Name}' takes no answer '{answer}': it matches none of its transitions on '{Field}'.",
            _ when !OnAnswer(considered) =>
                $"Step '{step.Name}' takes no answer '{answer}': its earlier transition on '{considered.Field}' to '{considered.Target}' would be taken instead.",
            _ when !Interpreter.Allows(considered, visits) =>
                $"Step '{step.Name}' takes no answer '{answer}': its transition to '{considered.Target}' on that answer reached its visit cap of {considered.VisitCap}.",
            _ => null,
        };
    }

    /// <summary>
    /// Every answer <see cref="Refusal"/> accepts at <paramref name="step"/>'s visit
    /// <paramref name="visits"/>, in table order: only a transition on <see cref="Field"/> with a
    /// string value can match an answer, so these are the values of those it accepts.
    /// </summary>
    public static IReadOnlyList<string> Accepted(FlowStep step, int visits) =>
    [
        .. step.Transitions
            .Where(OnAnswer)
            .Select(transition => transition.Value)
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Where(answer => Refusal(step, visits, answer) is null),
    ];

    /// <summary>The result a wait-for-human step records for <paramref name="answer"/>, which the check routes as the interpreter would.</summary>
    private static JsonDocument Result(string answer) =>
        JsonSerializer.SerializeToDocument(new JsonObject
        {
            ["verdict"] = "done",
            ["systemStep"] = CoreSystemSteps.WaitForHuman,
            [Field] = answer,
        });

    private static bool OnAnswer(FlowTransition transition) =>
        string.Equals(transition.Field, Field, StringComparison.Ordinal);
}
