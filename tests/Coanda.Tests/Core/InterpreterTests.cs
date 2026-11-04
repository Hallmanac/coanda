using System.Collections.Immutable;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;

namespace Coanda.Tests.Core;

public sealed class InterpreterTests
{
    private static FlowTransition On(string field, JsonElement value, string target, int cap = 5) =>
        new() { Field = field, Value = value, VisitCap = cap, Target = target };

    private static FlowTransition On(string verdict, string target, int cap = 5) => On("verdict", FlowValue.Of(verdict), target, cap);

    // A made-up flow: an action that starts it and a judgment, with a spawn cap of two.
    private static readonly FlowDefinition Sample = new()
    {
        Name = "sample",
        Accepts = ["thing"],
        Outcome = "thing-done",
        Budget = 1m,
        SpawnCap = 2,
        Steps =
        [
            new FlowStep
            {
                Name = "act",
                Action = "do-thing",
                Transitions =
                [
                    On("done", "judge"),
                    On("again", "act", cap: 1),
                    On("flag", FlowValue.Of(true), FlowDefinition.End),
                    On("count", FlowValue.Of(2), "judge"),
                    On("twice", "judge", cap: 1),
                    On("twice", FlowDefinition.End),
                ],
            },
            new FlowStep
            {
                Name = "judge",
                Agent = "an-agent",
                AnswerSchema = "a-schema",
                Transitions = [On("done", FlowDefinition.End), On("blocked", FlowDefinition.Pause), On("more", "judge"), On("act", "act")],
            },
        ],
    };

    private static JsonDocument Answer(string json) => JsonDocument.Parse(json);

    private static FlightMove Decide(FlightPosition position, string json)
    {
        using JsonDocument answer = Answer(json);
        return Interpreter.Decide(Sample, position, answer);
    }

    private static FlightPosition At(string step, int visits = 1, int spawns = 0) =>
        new(step, ImmutableDictionary<string, int>.Empty.Add(step, visits), spawns);

    [Fact]
    public void Start_and_two_moves_count_one_visit_to_each_step_entered_and_one_spawn_per_judgment()
    {
        FlightPosition start = Interpreter.Start(Sample);
        start.Step.Should().Be("act");
        start.VisitsTo("act").Should().Be(1);
        start.Spawns.Should().Be(0);

        FlightPosition judged = Interpreter.Apply(Sample, start, new FlightMove.GoTo("judge"));
        judged.Step.Should().Be("judge");
        judged.VisitsTo("act").Should().Be(1);
        judged.VisitsTo("judge").Should().Be(1);
        judged.Spawns.Should().Be(1);

        FlightPosition back = Interpreter.Apply(Sample, judged, new FlightMove.GoTo("act"));
        back.Step.Should().Be("act");
        back.VisitsTo("act").Should().Be(2);
        back.VisitsTo("judge").Should().Be(1);
        back.Spawns.Should().Be(1);
    }

    [Fact]
    public void Decide_leaves_its_inputs_unchanged_and_returns_equal_moves_for_equal_inputs()
    {
        FlightPosition position = Interpreter.Apply(Sample, Interpreter.Start(Sample), new FlightMove.GoTo("judge"));
        FlightPosition copy = position with { Visits = position.Visits.ToImmutableDictionary() };
        string hash = Sample.Hash;
        string json = FlowDefinition.CanonicalJson(Sample);

        FlightMove first = Decide(position, """{"verdict":"more"}""");
        FlightMove second = Decide(copy, """{"verdict":"more"}""");

        first.Should().Be(new FlightMove.GoTo("judge"));
        second.Should().Be(first);
        position.Should().Be(copy);
        position.Step.Should().Be("judge");
        position.VisitsTo("judge").Should().Be(1);
        position.Spawns.Should().Be(1);
        Sample.Hash.Should().Be(hash);
        FlowDefinition.CanonicalJson(Sample).Should().Be(json);
    }

    [Fact]
    public void Decide_and_apply_refuse_a_position_at_a_step_the_flow_does_not_have()
    {
        FlightPosition lost = At("nowhere");

        FluentActions.Invoking(() => Decide(lost, """{"verdict":"done"}"""))
            .Should().Throw<DomainException>().WithMessage("*'nowhere'*");
        FluentActions.Invoking(() => Interpreter.Apply(Sample, lost, new FlightMove.GoTo("judge")))
            .Should().Throw<DomainException>().WithMessage("*'nowhere'*");
    }

    [Fact]
    public void A_cap_of_one_fires_on_the_first_visit_and_pauses_on_the_second_naming_the_step_and_the_cap()
    {
        FlightPosition start = Interpreter.Start(Sample);

        FlightMove first = Decide(start, """{"verdict":"again"}""");
        first.Should().Be(new FlightMove.GoTo("act"));

        FlightPosition second = Interpreter.Apply(Sample, start, (FlightMove.GoTo)first);
        second.VisitsTo("act").Should().Be(2);
        Decide(second, """{"verdict":"again"}""").Should().BeOfType<FlightMove.Pause>()
            .Which.Reason.Should().Contain("'act'").And.Contain("visit cap of 1");
    }

    [Fact]
    public void The_first_matching_transition_decides_so_a_capped_one_pauses_rather_than_falling_through()
    {
        Decide(At("act", visits: 1), """{"verdict":"twice"}""").Should().Be(new FlightMove.GoTo("judge"));
        Decide(At("act", visits: 2), """{"verdict":"twice"}""").Should().BeOfType<FlightMove.Pause>()
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
        FlightMove move = Decide(Interpreter.Start(Sample), answer);

        if (matches)
        {
            move.Should().NotBeOfType<FlightMove.Pause>();
        }
        else
        {
            move.Should().BeOfType<FlightMove.Pause>().Which.Reason.Should().Contain("'act'");
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
        Decide(Interpreter.Start(Sample), answer).Should().BeOfType<FlightMove.Pause>()
            .Which.Reason.Should().Contain("'act'");
    }

    [Fact]
    public void A_transition_to_pause_pauses_naming_the_step_and_one_to_end_ends_with_the_flow_outcome()
    {
        FlightPosition judging = At("judge");

        Decide(judging, """{"verdict":"blocked"}""").Should().BeOfType<FlightMove.Pause>()
            .Which.Reason.Should().Contain("'judge'");
        Decide(judging, """{"verdict":"done"}""").Should().Be(new FlightMove.End("thing-done"));
    }

    [Fact]
    public void Going_to_a_judgment_at_the_spawn_cap_pauses_naming_the_cap_but_going_to_an_action_does_not()
    {
        FlightPosition below = At("judge", spawns: Sample.SpawnCap - 1);
        Decide(below, """{"verdict":"more"}""").Should().Be(new FlightMove.GoTo("judge"));

        FlightPosition atCap = At("judge", spawns: Sample.SpawnCap);
        Decide(atCap, """{"verdict":"more"}""").Should().BeOfType<FlightMove.Pause>()
            .Which.Reason.Should().Contain($"spawn cap of {Sample.SpawnCap}");
        Decide(atCap with { Spawns = Sample.SpawnCap + 1 }, """{"verdict":"more"}""").Should().BeOfType<FlightMove.Pause>();

        Decide(atCap, """{"verdict":"act"}""").Should().Be(new FlightMove.GoTo("act"));
    }
}
