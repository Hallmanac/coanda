using System.Text.Json;
using Coanda.Core.Contours;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

/// <summary>
/// Walks the shipped build contour through the interpreter with canned answers only. Step names,
/// routing values, and caps come from the contour's data rather than from literals here.
/// </summary>
public sealed class BuildContourInterpreterTests
{
    private static readonly Contour Build = ShippedContours.Build;

    /// <summary>A step's success answer is the one its first transition routes on.</summary>
    private static ContourTransition Success(string step) => Build.Step(step)?.Transitions[0]
        ?? throw new InvalidOperationException($"No step '{step}'.");

    /// <summary>
    /// The review that can send work back: the first judgment on the clean path with a later
    /// transition to another step, and that transition.
    /// </summary>
    private static (string Review, ContourTransition ToFix) ReviewToFix()
    {
        foreach (string name in CleanPath())
        {
            ContourStep step = Build.Step(name) ?? throw new InvalidOperationException($"No step '{name}'.");
            ContourTransition? back = step.Transitions.Skip(1).FirstOrDefault(t => Build.Step(t.Target) is not null);
            if (step.IsJudgment && back is not null)
            {
                return (name, back);
            }
        }

        throw new InvalidOperationException("The build contour has no review that sends work back.");
    }

    private static List<string> CleanPath()
    {
        (List<string> visited, _, _) = Walk((step, _) => Success(step));
        return visited;
    }

    private static JsonDocument Answer(ContourTransition routing) =>
        JsonSerializer.SerializeToDocument(new Dictionary<string, JsonElement> { [routing.Field] = routing.Value });

    /// <summary>Starts at the first step and answers each visit until the interpreter pauses or ends.</summary>
    private static (List<string> Visited, FlowRunMove Final, FlowRunPosition Last) Walk(Func<string, int, ContourTransition> answer)
    {
        FlowRunPosition position = Interpreter.Start(Build);
        List<string> visited = [position.Step];
        while (true)
        {
            using JsonDocument document = Answer(answer(position.Step, position.VisitsTo(position.Step)));
            FlowRunMove move = Interpreter.Decide(Build, position, document);
            if (move is not FlowRunMove.GoTo go)
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
        (_, ContourTransition toFix) = ReviewToFix();

        (List<string> visited, FlowRunMove final, _) = Walk((step, _) => Success(step));

        final.Should().Be(new FlowRunMove.End(Build.Outcome));
        visited[0].Should().Be(Build.First?.Name);
        visited.Should().NotContain(toFix.Target);
        visited.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Not_clean_on_every_visit_the_cap_allows_and_clean_after_reaches_end()
    {
        (string review, ContourTransition toFix) = ReviewToFix();

        (List<string> visited, FlowRunMove final, FlowRunPosition last) =
            Walk((step, visit) => step == review && visit <= toFix.VisitCap ? toFix : Success(step));

        // With the shipped caps the spawn cap is not reached first; this asserts it rather than assuming it.
        last.Spawns.Should().BeLessThanOrEqualTo(Build.SpawnCap);
        final.Should().Be(new FlowRunMove.End(Build.Outcome));
        visited.Count(s => s == toFix.Target).Should().Be(toFix.VisitCap);
        visited.Count(s => s == review).Should().Be(toFix.VisitCap + 1);
    }

    [Fact]
    public void One_more_not_clean_than_the_cap_allows_pauses_naming_the_review_and_the_cap()
    {
        (string review, ContourTransition toFix) = ReviewToFix();

        (List<string> visited, FlowRunMove final, FlowRunPosition last) =
            Walk((step, visit) => step == review && visit <= toFix.VisitCap + 1 ? toFix : Success(step));

        last.Step.Should().Be(review);
        last.Spawns.Should().BeLessThan(Build.SpawnCap);
        visited.Count(s => s == toFix.Target).Should().Be(toFix.VisitCap);
        final.Should().BeOfType<FlowRunMove.Pause>()
            .Which.Reason.Should().Contain($"'{review}'").And.Contain($"visit cap of {toFix.VisitCap}");
    }

    [Fact]
    public void An_answer_whose_value_no_transition_holds_pauses_naming_the_step()
    {
        ContourTransition unheard = Success(Build.First?.Name ?? "") with { Value = ContourValue.Of("unheard-of") };
        Build.Steps.SelectMany(s => s.Transitions).Should().NotContain(t => t.Value.ToString() == "unheard-of");

        (List<string> visited, FlowRunMove final, _) = Walk((_, _) => unheard);

        visited.Should().Equal(Build.First?.Name);
        final.Should().BeOfType<FlowRunMove.Pause>().Which.Reason.Should().Contain($"'{Build.First?.Name}'");
    }
}
