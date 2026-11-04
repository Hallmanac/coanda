using System.Collections.Immutable;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Contours;

namespace Coanda.Tests.Core;

public sealed class InterpreterTests
{
    private static ContourTransition On(string field, JsonElement value, string target, int cap = 5) =>
        new() { Field = field, Value = value, VisitCap = cap, Target = target };

    private static ContourTransition On(string verdict, string target, int cap = 5) => On("verdict", ContourValue.Of(verdict), target, cap);

    // A made-up contour: an action that starts it and a judgment, with a spawn cap of two.
    private static readonly Contour Sample = new()
    {
        Name = "sample",
        Accepts = ["thing"],
        Outcome = "thing-done",
        Budget = 1m,
        SpawnCap = 2,
        Steps =
        [
            new ContourStep
            {
                Name = "act",
                Action = "do-thing",
                Transitions =
                [
                    On("done", "judge"),
                    On("again", "act", cap: 1),
                    On("flag", ContourValue.Of(true), Contour.End),
                    On("count", ContourValue.Of(2), "judge"),
                    On("twice", "judge", cap: 1),
                    On("twice", Contour.End),
                ],
            },
            new ContourStep
            {
                Name = "judge",
                Agent = "an-agent",
                AnswerSchema = "a-schema",
                Transitions = [On("done", Contour.End), On("blocked", Contour.Pause), On("more", "judge"), On("act", "act")],
            },
        ],
    };

    private static JsonDocument Answer(string json) => JsonDocument.Parse(json);

    private static FlowRunMove Decide(FlowRunPosition position, string json)
    {
        using JsonDocument answer = Answer(json);
        return Interpreter.Decide(Sample, position, answer);
    }

    private static FlowRunPosition At(string step, int visits = 1, int spawns = 0) =>
        new(step, ImmutableDictionary<string, int>.Empty.Add(step, visits), spawns);

    [Fact]
    public void Start_and_two_moves_count_one_visit_to_each_step_entered_and_one_spawn_per_judgment()
    {
        FlowRunPosition start = Interpreter.Start(Sample);
        start.Step.Should().Be("act");
        start.VisitsTo("act").Should().Be(1);
        start.Spawns.Should().Be(0);

        FlowRunPosition judged = Interpreter.Apply(Sample, start, new FlowRunMove.GoTo("judge"));
        judged.Step.Should().Be("judge");
        judged.VisitsTo("act").Should().Be(1);
        judged.VisitsTo("judge").Should().Be(1);
        judged.Spawns.Should().Be(1);

        FlowRunPosition back = Interpreter.Apply(Sample, judged, new FlowRunMove.GoTo("act"));
        back.Step.Should().Be("act");
        back.VisitsTo("act").Should().Be(2);
        back.VisitsTo("judge").Should().Be(1);
        back.Spawns.Should().Be(1);
    }

    [Fact]
    public void Decide_leaves_its_inputs_unchanged_and_returns_equal_moves_for_equal_inputs()
    {
        FlowRunPosition position = Interpreter.Apply(Sample, Interpreter.Start(Sample), new FlowRunMove.GoTo("judge"));
        FlowRunPosition copy = position with { Visits = position.Visits.ToImmutableDictionary() };
        string hash = Sample.Hash;
        string json = Contour.CanonicalJson(Sample);

        FlowRunMove first = Decide(position, """{"verdict":"more"}""");
        FlowRunMove second = Decide(copy, """{"verdict":"more"}""");

        first.Should().Be(new FlowRunMove.GoTo("judge"));
        second.Should().Be(first);
        position.Should().Be(copy);
        position.Step.Should().Be("judge");
        position.VisitsTo("judge").Should().Be(1);
        position.Spawns.Should().Be(1);
        Sample.Hash.Should().Be(hash);
        Contour.CanonicalJson(Sample).Should().Be(json);
    }

    [Fact]
    public void Decide_and_apply_refuse_a_position_at_a_step_the_contour_does_not_have()
    {
        FlowRunPosition lost = At("nowhere");

        FluentActions.Invoking(() => Decide(lost, """{"verdict":"done"}"""))
            .Should().Throw<DomainException>().WithMessage("*'nowhere'*");
        FluentActions.Invoking(() => Interpreter.Apply(Sample, lost, new FlowRunMove.GoTo("judge")))
            .Should().Throw<DomainException>().WithMessage("*'nowhere'*");
    }

    [Fact]
    public void A_cap_of_one_fires_on_the_first_visit_and_pauses_on_the_second_naming_the_step_and_the_cap()
    {
        FlowRunPosition start = Interpreter.Start(Sample);

        FlowRunMove first = Decide(start, """{"verdict":"again"}""");
        first.Should().Be(new FlowRunMove.GoTo("act"));

        FlowRunPosition second = Interpreter.Apply(Sample, start, (FlowRunMove.GoTo)first);
        second.VisitsTo("act").Should().Be(2);
        Decide(second, """{"verdict":"again"}""").Should().BeOfType<FlowRunMove.Pause>()
            .Which.Reason.Should().Contain("'act'").And.Contain("visit cap of 1");
    }

    [Fact]
    public void The_first_matching_transition_decides_so_a_capped_one_pauses_rather_than_falling_through()
    {
        Decide(At("act", visits: 1), """{"verdict":"twice"}""").Should().Be(new FlowRunMove.GoTo("judge"));
        Decide(At("act", visits: 2), """{"verdict":"twice"}""").Should().BeOfType<FlowRunMove.Pause>()
            .Which.Reason.Should().Contain("'act'").And.Contain("visit cap of 1");
    }

    [Theory]
    [InlineData("""{"flag":true}""", true)]
    [InlineData("""{"flag":"true"}""", false)]
    [InlineData("""{"flag":false}""", false)]
    [InlineData("""{"flag":1}""", false)]
    [InlineData("""{"count":2}""", true)]
    [InlineData("""{"count":2.0}""", true)]
    [InlineData("""{"count":2e0}""", true)]
    [InlineData("""{"count":"2"}""", false)]
    [InlineData("""{"count":3}""", false)]
    [InlineData("""{"verdict":"done"}""", true)]
    [InlineData("""{"verdict":"Done"}""", false)]
    [InlineData("""{"verdict":"done "}""", false)]
    public void A_value_matches_only_an_equal_value_of_the_same_json_kind(string answer, bool matches)
    {
        FlowRunMove move = Decide(Interpreter.Start(Sample), answer);

        if (matches)
        {
            move.Should().NotBeOfType<FlowRunMove.Pause>();
        }
        else
        {
            move.Should().BeOfType<FlowRunMove.Pause>().Which.Reason.Should().Contain("'act'");
        }
    }

    [Theory]
    [InlineData("""{"verdict":"unheard-of"}""")]
    [InlineData("""{"other":"done"}""")]
    [InlineData("""{}""")]
    [InlineData("""["done"]""")]
    [InlineData("\"done\"")]
    [InlineData("null")]
    public void An_answer_no_transition_matches_pauses_naming_the_step(string answer)
    {
        Decide(Interpreter.Start(Sample), answer).Should().BeOfType<FlowRunMove.Pause>()
            .Which.Reason.Should().Contain("'act'");
    }

    [Fact]
    public void A_transition_to_pause_pauses_naming_the_step_and_one_to_end_ends_with_the_contour_outcome()
    {
        FlowRunPosition judging = At("judge");

        Decide(judging, """{"verdict":"blocked"}""").Should().BeOfType<FlowRunMove.Pause>()
            .Which.Reason.Should().Contain("'judge'");
        Decide(judging, """{"verdict":"done"}""").Should().Be(new FlowRunMove.End("thing-done"));
    }

    [Fact]
    public void Going_to_a_judgment_at_the_spawn_cap_pauses_naming_the_cap_but_going_to_an_action_does_not()
    {
        FlowRunPosition below = At("judge", spawns: Sample.SpawnCap - 1);
        Decide(below, """{"verdict":"more"}""").Should().Be(new FlowRunMove.GoTo("judge"));

        FlowRunPosition atCap = At("judge", spawns: Sample.SpawnCap);
        Decide(atCap, """{"verdict":"more"}""").Should().BeOfType<FlowRunMove.Pause>()
            .Which.Reason.Should().Contain($"spawn cap of {Sample.SpawnCap}");
        Decide(atCap with { Spawns = Sample.SpawnCap + 1 }, """{"verdict":"more"}""").Should().BeOfType<FlowRunMove.Pause>();

        Decide(atCap, """{"verdict":"act"}""").Should().Be(new FlowRunMove.GoTo("act"));
    }

    [Fact]
    public void A_step_can_fire_at_its_next_visit_while_any_transition_out_of_it_has_a_cap_no_smaller_than_that_visit()
    {
        ContourStep act = Sample.Step("act") ?? throw new InvalidOperationException("The sample has an act step.");

        Interpreter.CanFireAtNextVisit(act, 4).Should().BeTrue("the transitions capped at 5 allow a fifth visit");
        Interpreter.CanFireAtNextVisit(act, 5).Should().BeFalse("no transition out of the step allows a sixth visit");

        // The same rule Decide applies: at the fifth visit a transition capped at 5 still fires.
        Decide(At("act", visits: 5), """{"verdict":"done"}""").Should().Be(new FlowRunMove.GoTo("judge"));
        Decide(At("act", visits: 6), """{"verdict":"done"}""").Should().BeOfType<FlowRunMove.Pause>();
    }
}
