using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.SystemSteps;

namespace Coanda.Tests.Daemon.SystemSteps;

/// <summary>The wait-for-human step's answer to a dispatch: a pause until resumed, then done, carrying the person's answer.</summary>
public sealed class WaitForHumanSystemStepTests
{
    private static readonly WaitForHumanSystemStep Step = new();

    private static FlowTransition On(string field, JsonElement value, string target, int cap = 1) =>
        new() { Field = field, Value = value, VisitCap = cap, Target = target };

    private static FlowStep Hold(params FlowTransition[] transitions) => new()
    {
        Name = "ask",
        SystemStep = CoreSystemSteps.WaitForHuman,
        Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Answer it.") },
        Transitions = transitions,
    };

    private static readonly FlowStep PursueOrStop = Hold(
        On("answer", FlowValue.Of("pursue"), Flow.End), On("answer", FlowValue.Of("stop"), Flow.End), On("answer", FlowValue.Of(3), Flow.End));

    private static Task<SystemStepAnswer> DispatchAsync(FlowStep step, bool resumed, string? answer, int visit = 1)
    {
        var flow = new Flow { Name = "ask", Accepts = ["task"], Outcome = "asked", Budget = 1m, SpawnCap = 1, Steps = [step] };
        var request = new SystemStepDispatch(
            Principal.LocalOwner, "alpha", Ids.New(), Ids.New(), flow, step, resumed, visit, Ids.New(), "work", answer);
        return Step.DispatchAsync(request, CancellationToken.None).AsTask();
    }

    private static JsonElement Done(SystemStepAnswer answer) => Assert.IsType<SystemStepAnswer.Done>(answer).Result;

    [Fact]
    public async Task A_resumed_dispatch_with_an_answer_is_done_with_the_answer_beside_the_verdict_and_the_step()
    {
        JsonElement result = Done(await DispatchAsync(PursueOrStop, resumed: true, "pursue"));

        Assert.Equal(
            """{"verdict":"done","systemStep":"wait-for-human","answer":"pursue"}""",
            result.GetRawText());
    }

    [Fact]
    public async Task A_dispatch_not_yet_resumed_pauses_with_its_lever_whatever_its_transitions()
    {
        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(await DispatchAsync(PursueOrStop, resumed: false, null));

        Assert.Equal(("Step 'ask' waits for a person.", "Answer it."), (paused.Reason, paused.Lever));
    }

    [Fact]
    public async Task A_resumed_dispatch_without_an_answer_of_a_step_routing_on_one_pauses_again_naming_the_answers_it_takes()
    {
        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(await DispatchAsync(PursueOrStop, resumed: true, null));

        Assert.Equal("Step 'ask' waits for a person's answer; resume with one of: 'pursue', 'stop'.", paused.Reason);
        Assert.Equal("Answer it.", paused.Lever);
    }

    [Fact]
    public async Task A_re_pause_names_only_the_answers_the_visit_can_still_take()
    {
        FlowStep step = Hold(On("answer", FlowValue.Of("again"), "ask", cap: 1), On("answer", FlowValue.Of("done"), Flow.End, cap: 5));

        SystemStepAnswer.Paused second = Assert.IsType<SystemStepAnswer.Paused>(await DispatchAsync(step, resumed: true, null, visit: 2));
        SystemStepAnswer.Paused spent = Assert.IsType<SystemStepAnswer.Paused>(
            await DispatchAsync(Hold(On("answer", FlowValue.Of("again"), "ask", cap: 1)), resumed: true, null, visit: 2));

        Assert.Equal("Step 'ask' waits for a person's answer; resume with one of: 'done'.", second.Reason);
        Assert.Equal(
            "Step 'ask' waits for a person's answer, and none of its transitions on 'answer' can take one at this visit.",
            spent.Reason);
    }

    [Fact]
    public async Task A_resumed_dispatch_without_an_answer_of_a_step_routing_on_none_records_the_result_it_always_has()
    {
        JsonElement result = Done(await DispatchAsync(Hold(On("verdict", FlowValue.Of("done"), Flow.End)), resumed: true, null));

        Assert.Equal("""{"verdict":"done","systemStep":"wait-for-human"}""", result.GetRawText());
    }
}
