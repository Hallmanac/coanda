using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coanda.Core.Flows;

/// <summary>
/// The rule for a person's answer to a <see cref="CoreSystemSteps.WaitForHuman"/> step. The step
/// records the answer as the <see cref="Field"/> of its result, beside <c>verdict</c> set to
/// <c>done</c> and <c>systemStep</c> naming the step, and the flow routes on it through its
/// transition table. An answer is accepted only when the transition the interpreter would take at the
/// visit, for that result, is a transition on <see cref="Field"/> that fires there: its visit cap
/// allows the visit, and its target is no agent step the flow run's spawn cap forbids. So an accepted
/// answer never records and then pauses on a cap. The check asks the interpreter
/// (<see cref="Interpreter.Considered"/>, <see cref="Interpreter.Allows"/>,
/// <see cref="Interpreter.SpawnCapped"/>) rather than copying how it compares, so a transition whose
/// value is a number or a boolean matches no answer.
/// </summary>
public static class WaitForHumanAnswer
{
    /// <summary>The result field a wait-for-human step records a person's answer in.</summary>
    public const string Field = "answer";

    /// <summary>Whether any of <paramref name="step"/>'s transitions routes on <see cref="Field"/>.</summary>
    public static bool RoutesOnAnswer(FlowStep step) => step.Transitions.Any(OnAnswer);

    /// <summary>
    /// Why <paramref name="answer"/> is refused at <paramref name="step"/>'s visit
    /// <paramref name="visits"/>, counting the current visit, in <paramref name="flow"/>, whose flow
    /// run has spawned <paramref name="spawns"/> agents, or null when it is accepted.
    /// </summary>
    public static string? Refusal(Flow flow, FlowStep step, int visits, int spawns, string answer)
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
                $"Step '{step.Name}' takes no answer '{answer}': its transition on '{considered.Field}' to '{considered.Target}' is the first in its table to match, so it would be taken instead.",
            _ when !Interpreter.Allows(considered, visits) =>
                $"Step '{step.Name}' takes no answer '{answer}': its transition to '{considered.Target}' on that answer reached its visit cap of {considered.VisitCap}.",
            _ when Interpreter.SpawnCapped(flow, spawns, considered.Target) =>
                $"Step '{step.Name}' takes no answer '{answer}': its transition on that answer goes to agent step '{considered.Target}', and flow '{flow.Name}' reached its spawn cap of {flow.SpawnCap}.",
            _ => null,
        };
    }

    /// <summary>
    /// Every answer <see cref="Refusal"/> accepts at <paramref name="step"/>'s visit
    /// <paramref name="visits"/>, in table order: only a transition on <see cref="Field"/> with a
    /// string value can match an answer, so these are the values of those it accepts.
    /// </summary>
    public static IReadOnlyList<string> Accepted(Flow flow, FlowStep step, int visits, int spawns) =>
    [
        .. step.Transitions
            .Where(OnAnswer)
            .Select(transition => transition.Value)
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Where(answer => Refusal(flow, step, visits, spawns, answer) is null),
    ];

    /// <summary>
    /// Whether a resume that gave no answer leaves <paramref name="step"/> waiting for one: its
    /// transitions route on <see cref="Field"/>, and the result it records without an answer would
    /// fire no transition at the visit, because none matches it or the one that does is held by its
    /// visit cap or the spawn cap. A step whose result without an answer still fires a transition,
    /// a <c>verdict</c> transition among them, records that result and routes on it.
    /// </summary>
    public static bool AwaitsAnswer(Flow flow, FlowStep step, int visits, int spawns)
    {
        if (!RoutesOnAnswer(step))
        {
            return false;
        }

        using JsonDocument result = Result(null);
        FlowTransition? considered = Interpreter.Considered(step, result.RootElement);
        return considered is null
            || !Interpreter.Allows(considered, visits)
            || Interpreter.SpawnCapped(flow, spawns, considered.Target);
    }

    /// <summary>
    /// The result a wait-for-human step records for <paramref name="answer"/>, or with no
    /// <see cref="Field"/> when there is none, which the check routes as the interpreter would.
    /// </summary>
    private static JsonDocument Result(string? answer)
    {
        var result = new JsonObject
        {
            ["verdict"] = "done",
            ["systemStep"] = CoreSystemSteps.WaitForHuman,
        };
        if (answer is not null)
        {
            result[Field] = answer;
        }

        return JsonSerializer.SerializeToDocument(result);
    }

    private static bool OnAnswer(FlowTransition transition) =>
        string.Equals(transition.Field, Field, StringComparison.Ordinal);
}
