using System.Text.Json;
using Coanda.Core.Flows;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

/// <summary>
/// Walks the shipped build flow through the interpreter with canned answers only. Step names,
/// routing values, and caps come from the flow's data rather than from literals here.
/// </summary>
public sealed class BuildFlowInterpreterTests
{
    private static readonly FlowDefinition Build = ShippedFlows.Build;

    /// <summary>A step's success answer is the one its first transition routes on.</summary>
    private static FlowTransition Success(string step) => Build.Step(step)?.Transitions[0]
        ?? throw new InvalidOperationException($"No step '{step}'.");

    /// <summary>
    /// The review that can send work back: the first judgment on the clean path with a later
    /// transition to another step, and that transition.
    /// </summary>
    private static (string Review, FlowTransition ToFix) ReviewToFix()
    {
        foreach (string name in CleanPath())
        {
            FlowStep step = Build.Step(name) ?? throw new InvalidOperationException($"No step '{name}'.");
            FlowTransition? back = step.Transitions.Skip(1).FirstOrDefault(t => Build.Step(t.Target) is not null);
            if (step.IsJudgment && back is not null)
            {
                return (name, back);
            }
        }

        throw new InvalidOperationException("The build flow has no review that sends work back.");
    }

    private static List<string> CleanPath()
    {
        (List<string> visited, _, _) = Walk((step, _) => Success(step));
        return visited;
    }

    private static JsonDocument Answer(FlowTransition routing) =>
        JsonSerializer.SerializeToDocument(new Dictionary<string, JsonElement> { [routing.Field] = routing.Value });

    /// <summary>Starts at the first step and answers each visit until the interpreter parks or ends.</summary>
    private static (List<string> Visited, FlightMove Final, FlightPosition Last) Walk(Func<string, int, FlowTransition> answer)
    {
        FlightPosition position = Interpreter.Start(Build);
        List<string> visited = [position.Step];
        while (true)
        {
            using JsonDocument document = Answer(answer(position.Step, position.VisitsTo(position.Step)));
            FlightMove move = Interpreter.Decide(Build, position, document);
            if (move is not FlightMove.GoTo go)
            {
                return (visited, move, position);
            }

            position = Interpreter.Apply(Build, position, go);
            visited.Add(position.Step);
        }
    }

    [Fact]
    public void Success_at_every_step_reaches_end_with_the_outcome_and_never_enters_fix()
    {
        (_, FlowTransition toFix) = ReviewToFix();

        (List<string> visited, FlightMove final, _) = Walk((step, _) => Success(step));

        final.Should().Be(new FlightMove.End(Build.Outcome));
        visited[0].Should().Be(Build.First?.Name);
        visited.Should().NotContain(toFix.Target);
        visited.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Not_clean_on_every_visit_the_cap_allows_and_clean_after_reaches_end()
    {
        (string review, FlowTransition toFix) = ReviewToFix();

        (List<string> visited, FlightMove final, FlightPosition last) =
            Walk((step, visit) => step == review && visit <= toFix.VisitCap ? toFix : Success(step));

        // With the shipped caps the spawn cap is not reached first; this asserts it rather than assuming it.
        last.Spawns.Should().BeLessThanOrEqualTo(Build.SpawnCap);
        final.Should().Be(new FlightMove.End(Build.Outcome));
        visited.Count(s => s == toFix.Target).Should().Be(toFix.VisitCap);
        visited.Count(s => s == review).Should().Be(toFix.VisitCap + 1);
    }

    [Fact]
    public void One_more_not_clean_than_the_cap_allows_parks_naming_the_review_and_the_cap()
    {
        (string review, FlowTransition toFix) = ReviewToFix();

        (List<string> visited, FlightMove final, FlightPosition last) =
            Walk((step, visit) => step == review && visit <= toFix.VisitCap + 1 ? toFix : Success(step));

        last.Step.Should().Be(review);
        last.Spawns.Should().BeLessThan(Build.SpawnCap);
        visited.Count(s => s == toFix.Target).Should().Be(toFix.VisitCap);
        final.Should().BeOfType<FlightMove.Park>()
            .Which.Reason.Should().Contain($"'{review}'").And.Contain($"visit cap of {toFix.VisitCap}");
    }

    [Fact]
    public void An_answer_whose_value_no_transition_holds_parks_naming_the_step()
    {
        FlowTransition unheard = Success(Build.First?.Name ?? "") with { Value = FlowValue.Of("unheard-of") };
        Build.Steps.SelectMany(s => s.Transitions).Should().NotContain(t => t.Value.ToString() == "unheard-of");

        (List<string> visited, FlightMove final, _) = Walk((_, _) => unheard);

        visited.Should().Equal(Build.First?.Name);
        final.Should().BeOfType<FlightMove.Park>().Which.Reason.Should().Contain($"'{Build.First?.Name}'");
    }
}
