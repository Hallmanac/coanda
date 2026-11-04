using System.Collections.Immutable;
using System.Text.Json;

namespace Coanda.Core.Flows;

/// <summary>
/// Decides a flow run's next move from a flow, a position, and the last answer alone, as a pure
/// function: it reads no clock, store, file, or network, changes nothing it is given, and returns
/// equal moves for equal inputs. It routes on an answer field equalling a value and on visit
/// counts, and never computes. It assumes a flow that has already passed validation.
/// </summary>
public static class Interpreter
{
    /// <summary>The position of a flow run that has just entered the flow's first step.</summary>
    public static FlowRunPosition Start(Flow flow)
    {
        FlowStep first = flow.First
            ?? throw new DomainException($"Flow '{flow.Name}' has no steps, so a flow run has nowhere to start.");
        return Enter(first, ImmutableDictionary.Create<string, int>(StringComparer.Ordinal), spawns: 0);
    }

    /// <summary>
    /// The position after <paramref name="move"/>: one more visit to the step entered, and one more
    /// spawn when that step is an agent step.
    /// </summary>
    public static FlowRunPosition Apply(Flow flow, FlowRunPosition position, FlowRunMove.GoTo move)
    {
        _ = Current(flow, position);
        FlowStep next = flow.Step(move.Step)
            ?? throw new DomainException($"Flow '{flow.Name}' has no step '{move.Step}' to go to.");
        return Enter(next, position.Visits, position.Spawns);
    }

    /// <summary>
    /// The next move. The current step's transitions are read in table order and the first whose
    /// answer field is present and equal to its value is the one considered; it fires when the
    /// step's visit count, the current visit included, is no greater than its visit cap. Anything
    /// else pauses with a reason naming the step.
    /// </summary>
    public static FlowRunMove Decide(Flow flow, FlowRunPosition position, JsonDocument answer)
    {
        FlowStep step = Current(flow, position);
        JsonElement root = answer.RootElement;
        FlowTransition? considered = root.ValueKind == JsonValueKind.Object
            ? step.Transitions.FirstOrDefault(transition => Matches(root, transition))
            : null;
        int visits = position.VisitsTo(step.Name);

        return considered switch
        {
            null => new FlowRunMove.Pause(
                $"Step '{step.Name}' has no transition for that answer; no field and value in its table match it."),
            _ when !Allows(considered, visits) => new FlowRunMove.Pause(
                $"Step '{step.Name}' reached the visit cap of {considered.VisitCap} on its transition to '{considered.Target}'."),
            { Target: Flow.End } => new FlowRunMove.End(flow.Outcome),
            { Target: Flow.Pause } => new FlowRunMove.Pause(
                $"Step '{step.Name}' pauses on its answer, as its transition table says."),
            _ => GoTo(flow, position, considered.Target),
        };
    }

    /// <summary>
    /// Whether some transition out of <paramref name="step"/> could fire at the step's next visit,
    /// one more than <paramref name="visits"/>, by the same visit-cap rule <see cref="Decide"/>
    /// applies, whatever its answer or target. When none could, entering the step again can only
    /// pause on a visit cap.
    /// </summary>
    public static bool CanFireAtNextVisit(FlowStep step, int visits) =>
        step.Transitions.Any(transition => Allows(transition, visits + 1));

    /// <summary>A transition fires at a visit count no greater than its visit cap.</summary>
    private static bool Allows(FlowTransition transition, int visits) => visits <= transition.VisitCap;

    private static FlowRunMove GoTo(Flow flow, FlowRunPosition position, string target)
    {
        bool spawns = flow.Step(target) is { IsAgentStep: true };
        return spawns && position.Spawns >= flow.SpawnCap
            ? new FlowRunMove.Pause(
                $"Flow '{flow.Name}' reached its spawn cap of {flow.SpawnCap}; going to agent step '{target}' would spawn another agent.")
            : new FlowRunMove.GoTo(target);
    }

    private static FlowStep Current(Flow flow, FlowRunPosition position) =>
        flow.Step(position.Step)
            ?? throw new DomainException($"Flow '{flow.Name}' has no step '{position.Step}'; the position's current step must be one of its steps.");

    private static FlowRunPosition Enter(FlowStep step, ImmutableDictionary<string, int> visits, int spawns) =>
        new(
            step.Name,
            visits.SetItem(step.Name, visits.GetValueOrDefault(step.Name) + 1),
            step.IsAgentStep ? spawns + 1 : spawns);

    private static bool Matches(JsonElement answer, FlowTransition transition) =>
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
