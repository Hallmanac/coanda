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

    // A made-up flow: a system step that starts it and an agent step, with a spawn cap of two.
    private static readonly Flow Sample = new()
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
                SystemStep = "do-thing",
                Transitions =
                [
                    On("done", "judge"),
                    On("again", "act", cap: 1),
                    On("flag", FlowValue.Of(true), Flow.End),
                    On("count", FlowValue.Of(2), "judge"),
                    On("twice", "judge", cap: 1),
                    On("twice", Flow.End),
                ],
            },
            new FlowStep
            {
                Name = "judge",
                Agent = "an-agent",
                AnswerSchema = "a-schema",
                Transitions = [On("done", Flow.End), On("blocked", Flow.Pause), On("more", "judge"), On("act", "act")],
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
    public void Start_and_two_moves_count_one_visit_to_each_step_entered_and_one_spawn_per_agent_step()
    {
        FlowRunPosition start = Interpreter.Start(Sample);
        Assert.Equal("act", start.Step);
        Assert.Equal(1, start.VisitsTo("act"));
        Assert.Equal(0, start.Spawns);

        FlowRunPosition judged = Interpreter.Apply(Sample, start, new FlowRunMove.GoTo("judge"));
        Assert.Equal("judge", judged.Step);
        Assert.Equal(1, judged.VisitsTo("act"));
        Assert.Equal(1, judged.VisitsTo("judge"));
        Assert.Equal(1, judged.Spawns);

        FlowRunPosition back = Interpreter.Apply(Sample, judged, new FlowRunMove.GoTo("act"));
        Assert.Equal("act", back.Step);
        Assert.Equal(2, back.VisitsTo("act"));
        Assert.Equal(1, back.VisitsTo("judge"));
        Assert.Equal(1, back.Spawns);
    }

    [Fact]
    public void Decide_leaves_its_inputs_unchanged_and_returns_equal_moves_for_equal_inputs()
    {
        FlowRunPosition position = Interpreter.Apply(Sample, Interpreter.Start(Sample), new FlowRunMove.GoTo("judge"));
        FlowRunPosition copy = position with { Visits = position.Visits.ToImmutableDictionary() };
        string hash = Sample.Hash;
        string json = Flow.CanonicalJson(Sample);

        FlowRunMove first = Decide(position, """{"verdict":"more"}""");
        FlowRunMove second = Decide(copy, """{"verdict":"more"}""");

        Assert.Equal(new FlowRunMove.GoTo("judge"), first);
        Assert.Equal(first, second);
        Assert.Equal(copy, position);
        Assert.Equal("judge", position.Step);
        Assert.Equal(1, position.VisitsTo("judge"));
        Assert.Equal(1, position.Spawns);
        Assert.Equal(hash, Sample.Hash);
        Assert.Equal(json, Flow.CanonicalJson(Sample));
    }

    [Fact]
    public void Decide_and_apply_refuse_a_position_at_a_step_the_flow_does_not_have()
    {
        FlowRunPosition lost = At("nowhere");

        DomainException exception = Assert.Throws<DomainException>(() => Decide(lost, """{"verdict":"done"}"""));
        Assert.Contains("'nowhere'", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(() => Interpreter.Apply(Sample, lost, new FlowRunMove.GoTo("judge")));
        Assert.Contains("'nowhere'", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cap_of_one_fires_on_the_first_visit_and_pauses_on_the_second_naming_the_step_and_the_cap()
    {
        FlowRunPosition start = Interpreter.Start(Sample);

        FlowRunMove first = Decide(start, """{"verdict":"again"}""");
        Assert.Equal(new FlowRunMove.GoTo("act"), first);

        FlowRunPosition second = Interpreter.Apply(Sample, start, (FlowRunMove.GoTo)first);
        Assert.Equal(2, second.VisitsTo("act"));
        FlowRunMove.Pause pause = Assert.IsType<FlowRunMove.Pause>(Decide(second, """{"verdict":"again"}"""));
        Assert.Contains("'act'", pause.Reason, StringComparison.Ordinal);
        Assert.Contains("visit cap of 1", pause.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_matching_transition_decides_so_a_capped_one_pauses_rather_than_falling_through()
    {
        Assert.Equal(new FlowRunMove.GoTo("judge"), Decide(At("act", visits: 1), """{"verdict":"twice"}"""));
        FlowRunMove.Pause pause = Assert.IsType<FlowRunMove.Pause>(Decide(At("act", visits: 2), """{"verdict":"twice"}"""));
        Assert.Contains("'act'", pause.Reason, StringComparison.Ordinal);
        Assert.Contains("visit cap of 1", pause.Reason, StringComparison.Ordinal);
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
            Assert.IsNotType<FlowRunMove.Pause>(move);
        }
        else
        {
            Assert.Contains("'act'", Assert.IsType<FlowRunMove.Pause>(move).Reason, StringComparison.Ordinal);
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
        Assert.Contains("'act'", Assert.IsType<FlowRunMove.Pause>(Decide(Interpreter.Start(Sample), answer)).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transition_to_pause_pauses_naming_the_step_and_one_to_end_ends_with_the_flow_outcome()
    {
        FlowRunPosition judging = At("judge");

        Assert.Contains("'judge'", Assert.IsType<FlowRunMove.Pause>(Decide(judging, """{"verdict":"blocked"}""")).Reason, StringComparison.Ordinal);
        Assert.Equal(new FlowRunMove.End("thing-done"), Decide(judging, """{"verdict":"done"}"""));
    }

    [Fact]
    public void Going_to_an_agent_step_at_the_spawn_cap_pauses_naming_the_cap_but_going_to_a_system_step_does_not()
    {
        FlowRunPosition below = At("judge", spawns: Sample.SpawnCap - 1);
        Assert.Equal(new FlowRunMove.GoTo("judge"), Decide(below, """{"verdict":"more"}"""));

        FlowRunPosition atCap = At("judge", spawns: Sample.SpawnCap);
        Assert.Contains($"spawn cap of {Sample.SpawnCap}", Assert.IsType<FlowRunMove.Pause>(Decide(atCap, """{"verdict":"more"}""")).Reason, StringComparison.Ordinal);
        Assert.IsType<FlowRunMove.Pause>(Decide(atCap with { Spawns = Sample.SpawnCap + 1 }, """{"verdict":"more"}"""));

        Assert.Equal(new FlowRunMove.GoTo("act"), Decide(atCap, """{"verdict":"act"}"""));
    }

    [Fact]
    public void A_step_can_fire_at_its_next_visit_while_any_transition_out_of_it_has_a_cap_no_smaller_than_that_visit()
    {
        FlowStep act = Sample.Step("act") ?? throw new InvalidOperationException("The sample has an act step.");

        Assert.True(Interpreter.CanFireAtNextVisit(act, 4), "the transitions capped at 5 allow a fifth visit");
        Assert.False(Interpreter.CanFireAtNextVisit(act, 5), "no transition out of the step allows a sixth visit");

        // The same rule Decide applies: at the fifth visit a transition capped at 5 still fires.
        Assert.Equal(new FlowRunMove.GoTo("judge"), Decide(At("act", visits: 5), """{"verdict":"done"}"""));
        Assert.IsType<FlowRunMove.Pause>(Decide(At("act", visits: 6), """{"verdict":"done"}"""));
    }
}
