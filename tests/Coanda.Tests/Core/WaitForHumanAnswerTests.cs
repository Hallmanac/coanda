using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;

namespace Coanda.Tests.Core;

/// <summary>
/// Which answers a resume may give: only an unrecorded visit to a wait-for-human step takes one, and
/// only one the interpreter would route on at that visit, refused before anything is recorded.
/// </summary>
public sealed class WaitForHumanAnswerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static FlowTransition On(string field, JsonElement value, string target, int cap = 5) =>
        new() { Field = field, Value = value, VisitCap = cap, Target = target };

    private static FlowTransition OnAnswer(string answer, string target, int cap = 5) =>
        On(WaitForHumanAnswer.Field, FlowValue.Of(answer), target, cap);

    private static FlowStep Ask(params FlowTransition[] transitions) => new()
    {
        Name = "ask",
        SystemStep = CoreSystemSteps.WaitForHuman,
        Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Answer it.") },
        Transitions = transitions,
    };

    private static readonly FlowStep Later = new()
    {
        Name = "later",
        SystemStep = CoreSystemSteps.EmitOutcome,
        Transitions = [On("verdict", FlowValue.Of("done"), Flow.End)],
    };

    private static Flow FlowOf(params FlowStep[] steps) => new()
    {
        Name = "ask",
        Accepts = ["task"],
        Outcome = "asked",
        Budget = 1m,
        SpawnCap = 2,
        Steps = steps,
    };

    private static readonly Flow PursueOrStop = FlowOf(Ask(OnAnswer("pursue", "later"), OnAnswer("stop", Flow.End)), Later);

    private static FlowRun Started(Flow flow)
    {
        Parcel parcel = Parcel.Create(new ParcelCreated(Ids.New(), "alpha", "task", "hash", flow.Name, "A parcel", "owner", Now));
        (FlowRunStarted started, _) = FlowRunDecider.Start(flow, 1, parcel, "owner", Now);
        return FlowRun.Create(started);
    }

    private static FlowRun Paused(Flow flow)
    {
        FlowRun flowRun = Started(flow);
        Pause(flow, flowRun);
        return flowRun;
    }

    private static void Pause(Flow flow, FlowRun flowRun) =>
        flowRun.Apply(FlowRunDecider.Pause(flow, flowRun, "waiting", "answer it", "owner", Now));

    private static void RecordHuman(FlowRun flowRun, string answer) =>
        flowRun.Apply(new SystemStepCompleted(
            flowRun.Id, flowRun.CurrentStep, CoreSystemSteps.WaitForHuman,
            JsonSerializer.SerializeToElement(new { verdict = "done", systemStep = CoreSystemSteps.WaitForHuman, answer }), Now));

    private static string Refused(Flow flow, FlowRun flowRun, string answer)
    {
        DateTimeOffset updated = flowRun.UpdatedAt;
        DomainException refused = Assert.Throws<DomainException>(() => FlowRunDecider.Resume(flow, flowRun, null, answer, "owner", Now));
        // the decision records nothing and the flow run stays paused on the same visit
        Assert.Equal((FlowRunStatus.Paused, updated), (flowRun.Status, flowRun.UpdatedAt));
        return refused.Message;
    }

    [Theory]
    [InlineData("pursue")]
    [InlineData("stop")]
    [InlineData("  stop ")]
    public void An_answer_a_transition_on_the_answer_field_takes_is_accepted(string answer)
    {
        FlowRunResumed resumed = FlowRunDecider.Resume(PursueOrStop, Paused(PursueOrStop), null, answer, "owner", Now);

        Assert.Equal(answer.Trim(), resumed.Answer);
    }

    [Fact]
    public void An_answer_to_a_step_whose_transitions_route_on_no_answer_is_refused()
    {
        Flow flow = FlowOf(Ask(On("verdict", FlowValue.Of("done"), "later")), Later);

        Assert.Equal("Step 'ask' takes no answer: its transitions route on no answer.", Refused(flow, Paused(flow), "pursue"));
    }

    [Fact]
    public void An_answer_that_matches_none_of_the_transitions_on_the_answer_field_is_refused()
    {
        Assert.Equal(
            "Step 'ask' takes no answer 'maybe': it matches none of its transitions on 'answer'.",
            Refused(PursueOrStop, Paused(PursueOrStop), "maybe"));
        // answers compare ordinally
        Assert.Equal(
            "Step 'ask' takes no answer 'Pursue': it matches none of its transitions on 'answer'.",
            Refused(PursueOrStop, Paused(PursueOrStop), "Pursue"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void A_transition_on_the_answer_field_whose_value_is_a_number_or_a_boolean_matches_no_answer(string answer)
    {
        Flow flow = FlowOf(Ask(On(WaitForHumanAnswer.Field, FlowValue.Of(1), Flow.End), On(WaitForHumanAnswer.Field, FlowValue.Of(true), Flow.End)));

        Assert.Equal(
            $"Step 'ask' takes no answer '{answer}': it matches none of its transitions on 'answer'.",
            Refused(flow, Paused(flow), answer));
        Assert.Empty(WaitForHumanAnswer.Accepted(flow, flow.Steps[0], 1, 0));
    }

    [Fact]
    public void An_answer_an_earlier_transition_on_another_field_would_take_instead_is_refused()
    {
        Flow flow = FlowOf(Ask(On("verdict", FlowValue.Of("done"), Flow.End), OnAnswer("pursue", "later")), Later);

        Assert.Equal(
            "Step 'ask' takes no answer 'pursue': its transition on 'verdict' to 'end' is the first in its table to match, so it would be taken instead.",
            Refused(flow, Paused(flow), "pursue"));
        Assert.Empty(WaitForHumanAnswer.Accepted(flow, flow.Steps[0], 1, 0));
        // a resume without an answer still ends the run on the verdict transition
        Assert.False(WaitForHumanAnswer.AwaitsAnswer(flow, flow.Steps[0], 1, 0));
    }

    [Fact]
    public void An_answer_that_falls_through_to_a_later_transition_on_another_field_names_it_as_the_first_to_match()
    {
        Flow flow = FlowOf(Ask(OnAnswer("pursue", "later"), On("verdict", FlowValue.Of("done"), Flow.End)), Later);

        Assert.Equal(
            "Step 'ask' takes no answer 'other': its transition on 'verdict' to 'end' is the first in its table to match, so it would be taken instead.",
            Refused(flow, Paused(flow), "other"));
        Assert.Equal(["pursue"], WaitForHumanAnswer.Accepted(flow, flow.Steps[0], 1, 0));
        Assert.False(WaitForHumanAnswer.AwaitsAnswer(flow, flow.Steps[0], 1, 0));
    }

    [Fact]
    public void An_answer_whose_transition_goes_to_an_agent_step_past_the_spawn_cap_is_refused()
    {
        var work = new FlowStep
        {
            Name = "work",
            Agent = "an-agent",
            AnswerSchema = "a-schema",
            Transitions = [On("verdict", FlowValue.Of("done"), Flow.End)],
        };
        Flow flow = FlowOf(Ask(OnAnswer("work", "work"), OnAnswer("stop", Flow.End)), work);

        Assert.Equal(
            "Step 'ask' takes no answer 'work': its transition on that answer goes to agent step 'work', and flow 'ask' reached its spawn cap of 2.",
            WaitForHumanAnswer.Refusal(flow, flow.Steps[0], 1, 2, "work"));
        Assert.Null(WaitForHumanAnswer.Refusal(flow, flow.Steps[0], 1, 1, "work"));
        Assert.Equal(["stop"], WaitForHumanAnswer.Accepted(flow, flow.Steps[0], 1, 2));
        Assert.Equal(["work", "stop"], WaitForHumanAnswer.Accepted(flow, flow.Steps[0], 1, 1));
    }

    [Fact]
    public void A_step_awaits_an_answer_only_while_its_result_without_one_fires_no_transition()
    {
        Flow answersOnly = FlowOf(Ask(OnAnswer("pursue", "later")), Later);
        Flow capped = FlowOf(Ask(OnAnswer("again", "ask", cap: 1), On("verdict", FlowValue.Of("done"), Flow.End, cap: 1)));
        Flow verdictOnly = FlowOf(Ask(On("verdict", FlowValue.Of("done"), Flow.End)));

        Assert.True(WaitForHumanAnswer.AwaitsAnswer(answersOnly, answersOnly.Steps[0], 1, 0));
        // the verdict transition fires at visit 1 and is held by its visit cap at visit 2
        Assert.False(WaitForHumanAnswer.AwaitsAnswer(capped, capped.Steps[0], 1, 0));
        Assert.True(WaitForHumanAnswer.AwaitsAnswer(capped, capped.Steps[0], 2, 0));
        // a step that routes on no answer never waits for one
        Assert.False(WaitForHumanAnswer.AwaitsAnswer(verdictOnly, verdictOnly.Steps[0], 1, 0));
    }

    [Fact]
    public void An_answer_whose_transition_has_spent_its_visit_cap_is_refused_while_another_answer_is_accepted()
    {
        Flow flow = FlowOf(Ask(OnAnswer("again", "ask", cap: 1), OnAnswer("done", Flow.End)));
        FlowRun flowRun = Started(flow);
        RecordHuman(flowRun, "again");
        flowRun.Apply(FlowRunDecider.StartStep(flow, flowRun, "ask", Now));
        Pause(flow, flowRun);
        Assert.Equal(2, flowRun.Position.VisitsTo("ask"));

        Assert.Equal(
            "Step 'ask' takes no answer 'again': its transition to 'ask' on that answer reached its visit cap of 1.",
            Refused(flow, flowRun, "again"));
        Assert.Equal("done", FlowRunDecider.Resume(flow, flowRun, null, "done", "owner", Now).Answer);
        Assert.Equal(["done"], WaitForHumanAnswer.Accepted(flow, flow.Steps[0], 2, 0));
        Assert.Equal(["again", "done"], WaitForHumanAnswer.Accepted(flow, flow.Steps[0], 1, 0));
    }

    [Fact]
    public void An_answer_to_a_recorded_wait_for_human_visit_keeps_the_recorded_visit_refusal()
    {
        Flow flow = FlowOf(Ask(OnAnswer("pursue", Flow.Pause)));
        FlowRun flowRun = Started(flow);
        RecordHuman(flowRun, "pursue");
        Pause(flow, flowRun);

        Assert.Equal(
            $"Flow run {Ids.Short(flowRun.Id)}'s visit of step 'ask' has recorded, so the resume performs it again as a new visit and an answer would reach no step; resume without one.",
            Refused(flow, flowRun, "pursue"));
    }

    public static TheoryData<string> OtherSteps => ["agent", "module", "core", "core-recorded", "agent-recorded"];

    [Theory]
    [MemberData(nameof(OtherSteps))]
    public void An_answer_to_any_step_but_wait_for_human_is_refused_whether_or_not_its_visit_has_recorded(string shape)
    {
        FlowStep other = shape switch
        {
            "agent" or "agent-recorded" => new FlowStep
            {
                Name = "other",
                Agent = "an-agent",
                AnswerSchema = "a-schema",
                Transitions = [OnAnswer("pursue", Flow.Pause)],
            },
            "module" => new FlowStep { Name = "other", SystemStep = "software/gates", Transitions = [OnAnswer("pursue", Flow.Pause)] },
            _ => new FlowStep { Name = "other", SystemStep = CoreSystemSteps.EmitOutcome, Transitions = [OnAnswer("pursue", Flow.Pause)] },
        };
        Flow flow = FlowOf(other);
        FlowRun flowRun = Started(flow);
        switch (shape)
        {
            case "core-recorded":
                flowRun.Apply(new SystemStepCompleted(
                    flowRun.Id, "other", CoreSystemSteps.EmitOutcome, JsonSerializer.SerializeToElement(new { verdict = "done" }), Now));
                break;
            case "agent-recorded":
                flowRun.Apply(new AgentStepAnswered(
                    flowRun.Id, "other", "an-agent", "hash", new ModelSpec("vendor", "model", "high"),
                    JsonSerializer.SerializeToElement(new { verdict = "done" }), new Measurement(1, 0m, 1, TimeSpan.Zero), Now));
                break;
        }

        Pause(flow, flowRun);

        Assert.Equal(
            $"Flow run {Ids.Short(flowRun.Id)}'s current step 'other' is not a wait-for-human step; only a wait-for-human step takes an answer, so resume without one.",
            Refused(flow, flowRun, "pursue"));
        Assert.Null(FlowRunDecider.Resume(flow, flowRun, null, null, "owner", Now).Answer);
    }
}
