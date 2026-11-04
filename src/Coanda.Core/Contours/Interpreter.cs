using System.Collections.Immutable;
using System.Text.Json;

namespace Coanda.Core.Contours;

/// <summary>
/// Decides a flow's next move from a contour, a position, and the last answer alone, as a pure
/// function: it reads no clock, store, file, or network, changes nothing it is given, and returns
/// equal moves for equal inputs. It routes on an answer field equalling a value and on visit
/// counts, and never computes. It assumes a contour that has already passed validation.
/// </summary>
public static class Interpreter
{
    /// <summary>The position of a flow that has just entered the contour's first step.</summary>
    public static FlowPosition Start(Contour contour)
    {
        ContourStep first = contour.First
            ?? throw new DomainException($"Contour '{contour.Name}' has no steps, so a flow has nowhere to start.");
        return Enter(first, ImmutableDictionary.Create<string, int>(StringComparer.Ordinal), spawns: 0);
    }

    /// <summary>
    /// The position after <paramref name="move"/>: one more visit to the step entered, and one more
    /// spawn when that step is a judgment.
    /// </summary>
    public static FlowPosition Apply(Contour contour, FlowPosition position, FlowMove.GoTo move)
    {
        _ = Current(contour, position);
        ContourStep next = contour.Step(move.Step)
            ?? throw new DomainException($"Contour '{contour.Name}' has no step '{move.Step}' to go to.");
        return Enter(next, position.Visits, position.Spawns);
    }

    /// <summary>
    /// The next move. The current step's transitions are read in table order and the first whose
    /// answer field is present and equal to its value is the one considered; it fires when the
    /// step's visit count, the current visit included, is no greater than its visit cap. Anything
    /// else pauses with a reason naming the step.
    /// </summary>
    public static FlowMove Decide(Contour contour, FlowPosition position, JsonDocument answer)
    {
        ContourStep step = Current(contour, position);
        JsonElement root = answer.RootElement;
        ContourTransition? considered = root.ValueKind == JsonValueKind.Object
            ? step.Transitions.FirstOrDefault(transition => Matches(root, transition))
            : null;
        int visits = position.VisitsTo(step.Name);

        return considered switch
        {
            null => new FlowMove.Pause(
                $"Step '{step.Name}' has no transition for that answer; no field and value in its table match it."),
            _ when visits > considered.VisitCap => new FlowMove.Pause(
                $"Step '{step.Name}' reached the visit cap of {considered.VisitCap} on its transition to '{considered.Target}'."),
            { Target: Contour.End } => new FlowMove.End(contour.Outcome),
            { Target: Contour.Pause } => new FlowMove.Pause(
                $"Step '{step.Name}' pauses on its answer, as its transition table says."),
            _ => GoTo(contour, position, considered.Target),
        };
    }

    private static FlowMove GoTo(Contour contour, FlowPosition position, string target)
    {
        bool spawns = contour.Step(target) is { IsJudgment: true };
        return spawns && position.Spawns >= contour.SpawnCap
            ? new FlowMove.Pause(
                $"Contour '{contour.Name}' reached its spawn cap of {contour.SpawnCap}; going to judgment step '{target}' would spawn another agent.")
            : new FlowMove.GoTo(target);
    }

    private static ContourStep Current(Contour contour, FlowPosition position) =>
        contour.Step(position.Step)
            ?? throw new DomainException($"Contour '{contour.Name}' has no step '{position.Step}'; the position's current step must be one of its steps.");

    private static FlowPosition Enter(ContourStep step, ImmutableDictionary<string, int> visits, int spawns) =>
        new(
            step.Name,
            visits.SetItem(step.Name, visits.GetValueOrDefault(step.Name) + 1),
            step.IsJudgment ? spawns + 1 : spawns);

    private static bool Matches(JsonElement answer, ContourTransition transition) =>
        answer.TryGetProperty(transition.Field, out JsonElement value) && Equal(value, transition.Value);

    /// <summary>
    /// A string equals only an equal string, compared ordinally; a number only a number of equal
    /// numeric value; a boolean only the same boolean. Nothing converts between kinds.
    /// </summary>
    private static bool Equal(JsonElement answer, JsonElement expected) => (answer.ValueKind, expected.ValueKind) switch
    {
        (JsonValueKind.String, JsonValueKind.String) =>
            string.Equals(answer.GetString(), expected.GetString(), StringComparison.Ordinal),
        (JsonValueKind.Number, JsonValueKind.Number) => NumbersEqual(answer, expected),
        (JsonValueKind.True, JsonValueKind.True) or (JsonValueKind.False, JsonValueKind.False) => true,
        _ => false,
    };

    private static bool NumbersEqual(JsonElement left, JsonElement right) =>
        left.TryGetDecimal(out decimal leftDecimal) && right.TryGetDecimal(out decimal rightDecimal)
            ? leftDecimal == rightDecimal
            : left.TryGetDouble(out double leftDouble) && right.TryGetDouble(out double rightDouble) && leftDouble == rightDouble;
}
