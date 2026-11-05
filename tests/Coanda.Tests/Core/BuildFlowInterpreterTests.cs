using System.Text.Json;
using Coanda.Core.Flows;

namespace Coanda.Tests.Core;

/// <summary>
/// Walks the shipped build flow through the interpreter with canned answers only. Step names,
/// routing values, and caps come from the flow's data rather than from literals here.
/// </summary>
public sealed class BuildFlowInterpreterTests
{
    private static readonly Flow Build = SoftwareFlows.Build;

    /// <summary>A step's success answer is the one its first transition routes on.</summary>
    private static FlowTransition Success(string step) => Build.Step(step)?.Transitions[0]
        ?? throw new InvalidOperationException($"No step '{step}'.");

    /// <summary>
    /// The review that can send work back: the first agent step on the clean path with a later
    /// transition to another step, and that transition.
    /// </summary>
    private static (string Review, FlowTransition ToFix) ReviewToFix()
    {
        foreach (string name in CleanPath())
        {
            FlowStep step = Build.Step(name) ?? throw new InvalidOperationException($"No step '{name}'.");
            FlowTransition? back = step.Transitions.Skip(1).FirstOrDefault(t => Build.Step(t.Target) is not null);
            if (step.IsAgentStep && back is not null)
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

    /// <summary>Starts at the first step and answers each visit until the interpreter pauses or ends.</summary>
    private static (List<string> Visited, FlowRunMove Final, FlowRunPosition Last) Walk(Func<string, int, FlowTransition> answer)
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
        (_, FlowTransition toFix) = ReviewToFix();

        (List<string> visited, FlowRunMove final, _) = Walk((step, _) => Success(step));

        Assert.Equal(new FlowRunMove.End(Build.Outcome), final);
        Assert.Equal(Build.First?.Name, visited[0]);
        Assert.DoesNotContain(toFix.Target, visited);
        Assert.Distinct(visited);
    }

    [Fact]
    public void Not_clean_on_every_visit_the_cap_allows_and_clean_after_reaches_end()
    {
        (string review, FlowTransition toFix) = ReviewToFix();

        (List<string> visited, FlowRunMove final, FlowRunPosition last) =
            Walk((step, visit) => step == review && visit <= toFix.VisitCap ? toFix : Success(step));

        // With the shipped caps the spawn cap is not reached first; this asserts it rather than assuming it.
        Assert.True(last.Spawns <= Build.SpawnCap, $"Expected at most {Build.SpawnCap} spawns, but found {last.Spawns}.");
        Assert.Equal(new FlowRunMove.End(Build.Outcome), final);
        Assert.Equal(toFix.VisitCap, visited.Count(s => s == toFix.Target));
        Assert.Equal(toFix.VisitCap + 1, visited.Count(s => s == review));
    }

    [Fact]
    public void One_more_not_clean_than_the_cap_allows_pauses_naming_the_review_and_the_cap()
    {
        (string review, FlowTransition toFix) = ReviewToFix();

        (List<string> visited, FlowRunMove final, FlowRunPosition last) =
            Walk((step, visit) => step == review && visit <= toFix.VisitCap + 1 ? toFix : Success(step));

        Assert.Equal(review, last.Step);
        Assert.True(last.Spawns < Build.SpawnCap, $"Expected fewer than {Build.SpawnCap} spawns, but found {last.Spawns}.");
        Assert.Equal(toFix.VisitCap, visited.Count(s => s == toFix.Target));
        FlowRunMove.Pause pause = Assert.IsType<FlowRunMove.Pause>(final);
        Assert.Contains($"'{review}'", pause.Reason, StringComparison.Ordinal);
        Assert.Contains($"visit cap of {toFix.VisitCap}", pause.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_whose_value_no_transition_holds_pauses_naming_the_step()
    {
        FlowTransition unheard = Success(Build.First?.Name ?? "") with { Value = FlowValue.Of("unheard-of") };
        Assert.DoesNotContain(Build.Steps.SelectMany(s => s.Transitions), t => t.Value.ToString() == "unheard-of");

        (List<string> visited, FlowRunMove final, _) = Walk((_, _) => unheard);

        Assert.Equal<string?>([Build.First?.Name], visited);
        Assert.Contains($"'{Build.First?.Name}'", Assert.IsType<FlowRunMove.Pause>(final).Reason, StringComparison.Ordinal);
    }
}
