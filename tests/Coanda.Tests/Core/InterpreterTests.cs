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

    private static FlowMove Decide(FlowPosition position, string json)
    {
        using JsonDocument answer = Answer(json);
        return Interpreter.Decide(Sample, position, answer);
    }

    private static FlowPosition At(string step, int visits = 1, int spawns = 0) =>
        new(step, ImmutableDictionary<string, int>.Empty.Add(step, visits), spawns);

    [Fact]
    public void Start_and_two_moves_count_one_visit_to_each_step_entered_and_one_spawn_per_judgment()
    {
        FlowPosition start = Interpreter.Start(Sample);
        start.Step.Should().Be("act");
        start.VisitsTo("act").Should().Be(1);
        start.Spawns.Should().Be(0);

        FlowPosition judged = Interpreter.Apply(Sample, start, new FlowMove.GoTo("judge"));
        judged.Step.Should().Be("judge");
        judged.VisitsTo("act").Should().Be(1);
        judged.VisitsTo("judge").Should().Be(1);
        judged.Spawns.Should().Be(1);

        FlowPosition back = Interpreter.Apply(Sample, judged, new FlowMove.GoTo("act"));
        back.Step.Should().Be("act");
        back.VisitsTo("act").Should().Be(2);
        back.VisitsTo("judge").Should().Be(1);
        back.Spawns.Should().Be(1);
    }

    [Fact]
    public void Decide_leaves_its_inputs_unchanged_and_returns_equal_moves_for_equal_inputs()
    {
        FlowPosition position = Interpreter.Apply(Sample, Interpreter.Start(Sample), new FlowMove.GoTo("judge"));
        FlowPosition copy = position with { Visits = position.Visits.ToImmutableDictionary() };
        string hash = Sample.Hash;
        string json = Contour.CanonicalJson(Sample);

        FlowMove first = Decide(position, """{"verdict":"more"}""");
        FlowMove second = Decide(copy, """{"verdict":"more"}""");

        first.Should().Be(new FlowMove.GoTo("judge"));
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
        FlowPosition lost = At("nowhere");

        FluentActions.Invoking(() => Decide(lost, """{"verdict":"done"}"""))
            .Should().Throw<DomainException>().WithMessage("*'nowhere'*");
        FluentActions.Invoking(() => Interpreter.Apply(Sample, lost, new FlowMove.GoTo("judge")))
            .Should().Throw<DomainException>().WithMessage("*'nowhere'*");
    }

    [Fact]
    public void A_cap_of_one_fires_on_the_first_visit_and_pauses_on_the_second_naming_the_step_and_the_cap()
    {
        FlowPosition start = Interpreter.Start(Sample);

        FlowMove first = Decide(start, """{"verdict":"again"}""");
        first.Should().Be(new FlowMove.GoTo("act"));

        FlowPosition second = Interpreter.Apply(Sample, start, (FlowMove.GoTo)first);
        second.VisitsTo("act").Should().Be(2);
        Decide(second, """{"verdict":"again"}""").Should().BeOfType<FlowMove.Pause>()
            .Which.Reason.Should().Contain("'act'").And.Contain("visit cap of 1");
    }

    [Fact]
    public void The_first_matching_transition_decides_so_a_capped_one_pauses_rather_than_falling_through()
    {
        Decide(At("act", visits: 1), """{"verdict":"twice"}""").Should().Be(new FlowMove.GoTo("judge"));
        Decide(At("act", visits: 2), """{"verdict":"twice"}""").Should().BeOfType<FlowMove.Pause>()
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
        FlowMove move = Decide(Interpreter.Start(Sample), answer);

        if (matches)
        {
            move.Should().NotBeOfType<FlowMove.Pause>();
        }
        else
        {
            move.Should().BeOfType<FlowMove.Pause>().Which.Reason.Should().Contain("'act'");
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
        Decide(Interpreter.Start(Sample), answer).Should().BeOfType<FlowMove.Pause>()
            .Which.Reason.Should().Contain("'act'");
    }

    [Fact]
    public void A_transition_to_pause_pauses_naming_the_step_and_one_to_end_ends_with_the_contour_outcome()
    {
        FlowPosition judging = At("judge");

        Decide(judging, """{"verdict":"blocked"}""").Should().BeOfType<FlowMove.Pause>()
            .Which.Reason.Should().Contain("'judge'");
        Decide(judging, """{"verdict":"done"}""").Should().Be(new FlowMove.End("thing-done"));
    }

    [Fact]
    public void Going_to_a_judgment_at_the_spawn_cap_pauses_naming_the_cap_but_going_to_an_action_does_not()
    {
        FlowPosition below = At("judge", spawns: Sample.SpawnCap - 1);
        Decide(below, """{"verdict":"more"}""").Should().Be(new FlowMove.GoTo("judge"));

        FlowPosition atCap = At("judge", spawns: Sample.SpawnCap);
        Decide(atCap, """{"verdict":"more"}""").Should().BeOfType<FlowMove.Pause>()
            .Which.Reason.Should().Contain($"spawn cap of {Sample.SpawnCap}");
        Decide(atCap with { Spawns = Sample.SpawnCap + 1 }, """{"verdict":"more"}""").Should().BeOfType<FlowMove.Pause>();

        Decide(atCap, """{"verdict":"act"}""").Should().Be(new FlowMove.GoTo("act"));
    }
}
