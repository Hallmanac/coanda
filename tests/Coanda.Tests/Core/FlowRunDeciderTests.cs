using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class FlowRunDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Flow Build = ShippedFlows.Build;
    private static readonly ModelSpec Model = new("anthropic", "claude-opus-5-5", "high");
    private static readonly Measurement Spend = new(1200, 0.42m, 3, TimeSpan.FromSeconds(63));

    private static Parcel ParcelOf(string type = "task") =>
        Parcel.Create(new ParcelCreated(Ids.New(), "alpha", type, "hash", "build", "A parcel", "owner", Now));

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// A well-formed answer from the build agent for <paramref name="step"/>, from the session the
    /// visit expects: the flow run's current judgment is launched and its session opened first when it
    /// holds none.
    /// </summary>
    private static JudgmentAnswered AnswerAt(FlowRun flowRun, string step) =>
        FlowRunDecider.Answer(Build, flowRun, step, "build", Launched(flowRun), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now);

    /// <summary>The session the flow run's current visit expects, launching it and opening one when an unrecorded running judgment holds none.</summary>
    private static Guid Launched(FlowRun flowRun)
    {
        if (flowRun is { Status: FlowRunStatus.Running, Current: { Kind: StepKind.Judgment, IsRecorded: false, SessionId: null } current })
        {
            flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, current.Step, "agenthash", Model, Now));
            flowRun.Apply(FlowRunDecider.OpenSession(Build, flowRun, current.Step, Ids.New(), Now));
        }

        return flowRun.Current?.SessionId ?? Ids.New();
    }

    /// <summary>A build flow run started on a fresh parcel, standing at its first step, the worktree action.</summary>
    private static FlowRun Started(Flow? flow = null)
    {
        (FlowRunStarted started, _) = FlowRunDecider.Start(flow ?? Build, 1, ParcelOf(), "owner", Now);
        return FlowRun.Create(started);
    }

    /// <summary>A build flow run at its judgment step, 'build'.</summary>
    private static FlowRun AtJudgment()
    {
        FlowRun flowRun = Started();
        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "build", Now));
        return flowRun;
    }

    private static FlowRun InStatus(FlowRunStatus status)
    {
        FlowRun flowRun = Started();
        switch (status)
        {
            case FlowRunStatus.Paused:
                flowRun.Apply(FlowRunDecider.Pause(Build, flowRun, "waiting", "answer the question", "owner", Now));
                break;
            case FlowRunStatus.Ended:
                flowRun.Apply(FlowRunDecider.End(Build, flowRun, Build.Outcome, "owner", Now).Ended);
                break;
            case FlowRunStatus.Stopped:
                flowRun.Apply(FlowRunDecider.Stop(flowRun, "no longer wanted", "owner", Now).Stopped);
                break;
        }

        return flowRun;
    }

    [Fact]
    public void Replaying_a_start_and_two_step_starts_yields_the_interpreters_position()
    {
        FlowRun flowRun = Started();
        flowRun.Position.Should().Be(Interpreter.Start(Build), "a started flow run has already visited its first step once");

        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "build", Now));
        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "gates", Now));

        FlowRunPosition expected = Interpreter.Apply(
            Build, Interpreter.Apply(Build, Interpreter.Start(Build), new FlowRunMove.GoTo("build")), new FlowRunMove.GoTo("gates"));
        flowRun.Position.Should().Be(expected);
        flowRun.Position.Step.Should().Be("gates");
        flowRun.Position.Spawns.Should().Be(1, "only the judgment step 'build' spawned an agent");
        flowRun.Position.VisitsTo("worktree").Should().Be(1);
        flowRun.Position.VisitsTo("build").Should().Be(1);
        flowRun.Position.VisitsTo("gates").Should().Be(1);
    }

    [Fact]
    public void A_flow_run_whose_first_step_is_a_judgment_counts_one_spawn_at_start()
    {
        FlowRun flowRun = Started(ShippedFlows.ReviewOnAssignment);

        flowRun.Position.Should().Be(Interpreter.Start(ShippedFlows.ReviewOnAssignment));
        flowRun.Position.Spawns.Should().Be(1);
    }

    [Fact]
    public void Replay_reads_the_events_alone_and_never_the_flow()
    {
        FlowRun flowRun = FlowRun.Create(new FlowRunStarted(
            Ids.New(), Ids.New(), "any", 1, "h", "owner", "ask", StepKind.Judgment, "an-agent", Now));
        flowRun.Apply(new StepStarted(flowRun.Id, "do", StepKind.Action, "an-action", Now));
        flowRun.Apply(new StepStarted(flowRun.Id, "ask", StepKind.Judgment, "an-agent", Now));

        flowRun.Position.Step.Should().Be("ask");
        flowRun.Position.VisitsTo("ask").Should().Be(2);
        flowRun.Position.Spawns.Should().Be(2);
    }

    [Fact]
    public void A_start_attaches_the_flow_run_to_its_parcel_and_records_the_flow_and_first_step()
    {
        Parcel parcel = ParcelOf();
        (FlowRunStarted started, ParcelFlowRunAttached attached) = FlowRunDecider.Start(Build, 3, parcel, "owner", Now);

        started.ParcelId.Should().Be(parcel.Id);
        started.FlowName.Should().Be("build");
        started.FlowVersion.Should().Be(3);
        started.FlowHash.Should().Be(Build.Hash);
        started.FirstStep.Should().Be("worktree");
        started.FirstStepKind.Should().Be(StepKind.Action);
        started.FirstStepPerformer.Should().Be(SoftwareActions.Worktree);
        attached.FlowRunId.Should().Be(started.FlowRunId);
        attached.ParcelId.Should().Be(parcel.Id);
    }

    [Fact]
    public void A_start_on_a_closed_parcel_is_refused()
    {
        Parcel parcel = ParcelOf();
        parcel.Apply(new ParcelClosed(parcel.Id, "build", "closed", "abandoned", "Not needed.", "owner", Now));

        Action start = () => FlowRunDecider.Start(Build, 1, parcel, "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("*is closed (abandoned); a flow run starts only on an open parcel*");
    }

    [Fact]
    public void A_start_on_a_parcel_whose_type_the_flow_does_not_accept_is_refused()
    {
        Action start = () => FlowRunDecider.Start(Build, 1, ParcelOf("idea"), "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("Flow 'build' accepts parcels of type task, not 'idea'.");
    }

    [Fact]
    public void A_start_on_a_parcel_that_already_has_a_live_flow_run_is_refused()
    {
        Parcel parcel = ParcelOf();
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, Ids.New(), Now));

        Action start = () => FlowRunDecider.Start(Build, 1, parcel, "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("A parcel has at most one live flow run;*");
    }

    public static TheoryData<FlowRunStatus, string> NotRunning()
    {
        var data = new TheoryData<FlowRunStatus, string>();
        foreach (FlowRunStatus status in new[] { FlowRunStatus.Paused, FlowRunStatus.Ended, FlowRunStatus.Stopped })
        {
            foreach (string intent in new[] { "step", "answer", "result", "pause", "end" })
            {
                data.Add(status, intent);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NotRunning))]
    public void Recording_on_a_flow_run_that_is_not_running_is_refused(FlowRunStatus status, string intent)
    {
        FlowRun flowRun = InStatus(status);

        Action record = intent switch
        {
            "step" => () => FlowRunDecider.StartStep(Build, flowRun, "build", Now),
            "answer" => () => FlowRunDecider.Answer(Build, flowRun, "worktree", "build", Ids.New(), "agenthash", Model, Json("{}"), Spend, Now),
            "result" => () => FlowRunDecider.Complete(Build, flowRun, "worktree", SoftwareActions.Worktree, Json("{}"), Now),
            "pause" => () => FlowRunDecider.Pause(Build, flowRun, "why", "lever", "owner", Now),
            "end" => () => FlowRunDecider.End(Build, flowRun, Build.Outcome, "owner", Now),
            _ => throw new ArgumentOutOfRangeException(nameof(intent)),
        };

        record.Should().Throw<DomainException>().WithMessage("*; only a running flow run can *");
    }

    [Fact]
    public void Starting_a_step_the_flow_does_not_have_is_refused()
    {
        Action step = () => FlowRunDecider.StartStep(Build, Started(), "deploy", Now);

        step.Should().Throw<DomainException>().WithMessage("Flow 'build' has no step 'deploy'; a flow run enters only a step its flow has.");
    }

    [Fact]
    public void An_answer_for_a_step_other_than_the_current_one_is_refused()
    {
        Action answer = () => AnswerAt(AtJudgment(), "fix");

        answer.Should().Throw<DomainException>().WithMessage("*is at step 'build'; an answer is recorded only for the current step, not 'fix'.");
    }

    [Fact]
    public void A_result_for_a_step_other_than_the_current_one_is_refused()
    {
        Action result = () => FlowRunDecider.Complete(Build, Started(), "gates", SoftwareActions.Gates, Json("{}"), Now);

        result.Should().Throw<DomainException>().WithMessage("*is at step 'worktree'; a result is recorded only for the current step, not 'gates'.");
    }

    [Fact]
    public void An_answer_on_an_action_step_is_refused()
    {
        Action answer = () => FlowRunDecider.Answer(Build, Started(), "worktree", "build", Ids.New(), "agenthash", Model, Json("{}"), Spend, Now);

        answer.Should().Throw<DomainException>().WithMessage("Step 'worktree' is an action step; it takes a result, not an answer.");
    }

    [Fact]
    public void A_result_on_a_judgment_step_is_refused()
    {
        Action result = () => FlowRunDecider.Complete(Build, AtJudgment(), "build", "build", Json("{}"), Now);

        result.Should().Throw<DomainException>().WithMessage("Step 'build' is a judgment step; it takes an answer, not a result.");
    }

    [Fact]
    public void An_answer_records_the_agent_its_hash_the_model_the_document_and_the_measurement()
    {
        FlowRun flowRun = AtJudgment();
        JudgmentAnswered answered = AnswerAt(flowRun, "build");
        flowRun.Apply(answered);

        answered.Agent.Should().Be("build");
        answered.AgentHash.Should().Be("agenthash");
        answered.Model.Should().Be(Model);
        answered.Answer.GetProperty("verdict").GetString().Should().Be("done");
        answered.Measurement.Should().Be(Spend);
        FlowRunStepEntry? current = flowRun.Current;
        current.Should().NotBeNull();
        current.Answer.HasValue.Should().BeTrue();
        current.Answer.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("done");
        current.Measurement.Should().Be(Spend);
    }

    [Fact]
    public void A_second_answer_on_the_same_visit_is_refused()
    {
        FlowRun flowRun = AtJudgment();
        flowRun.Apply(AnswerAt(flowRun, "build"));

        Action again = () => AnswerAt(flowRun, "build");

        again.Should().Throw<DomainException>().WithMessage("Step 'build' already holds what this visit recorded*");
    }

    [Fact]
    public void An_answer_from_an_agent_the_step_does_not_name_is_refused()
    {
        Action answer = () => FlowRunDecider.Answer(Build, AtJudgment(), "build", "fix", Ids.New(), "agenthash", Model, Json("{}"), Spend, Now);

        answer.Should().Throw<DomainException>().WithMessage("Step 'build' names the agent 'build'; an answer from 'fix' is not its answer.");
    }

    [Fact]
    public void A_negative_measurement_is_refused()
    {
        Action answer = () => FlowRunDecider.Answer(
            Build, AtJudgment(), "build", "build", Ids.New(), "agenthash", Model, Json("{}"), Spend with { CostUsd = -1m }, Now);

        answer.Should().Throw<DomainException>().WithMessage("A measurement is never negative*");
    }

    [Theory]
    [InlineData("", "answer the question", "A pause records why the flow run stopped; give a reason.")]
    [InlineData("waiting", " ", "A pause records the lever a person acts on to resume the flow run; give a lever.")]
    public void A_pause_with_an_empty_reason_or_lever_is_refused(string reason, string lever, string message)
    {
        Action pause = () => FlowRunDecider.Pause(Build, Started(), reason, lever, "owner", Now);

        pause.Should().Throw<DomainException>().WithMessage(message);
    }

    [Fact]
    public void An_end_with_an_outcome_other_than_the_flows_is_refused()
    {
        Action end = () => FlowRunDecider.End(Build, Started(), "merged", "owner", Now);

        end.Should().Throw<DomainException>().WithMessage("Flow 'build' ends with its declared outcome 'pr-opened', not 'merged'.");
    }

    [Theory]
    [InlineData(FlowRunStatus.Running)]
    [InlineData(FlowRunStatus.Ended)]
    [InlineData(FlowRunStatus.Stopped)]
    public void Resuming_a_flow_run_that_is_not_paused_is_refused(FlowRunStatus status)
    {
        Action resume = () => FlowRunDecider.Resume(Build, InStatus(status), "go on", "owner", Now);

        resume.Should().Throw<DomainException>().WithMessage("*; only a paused flow run resumes.");
    }

    [Fact]
    public void A_paused_flow_run_resumes_and_runs_again()
    {
        FlowRun flowRun = InStatus(FlowRunStatus.Paused);
        flowRun.IsLive.Should().BeTrue("a paused flow run is still live");

        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, "answered", "owner", Now));

        flowRun.Status.Should().Be(FlowRunStatus.Running);
        flowRun.Lever.Should().BeNull();
    }

    [Theory]
    [InlineData(FlowRunStatus.Ended, "has ended")]
    [InlineData(FlowRunStatus.Stopped, "has been stopped")]
    public void Stopping_a_flow_run_that_has_ended_or_stopped_is_refused(FlowRunStatus status, string described)
    {
        Action stop = () => FlowRunDecider.Stop(InStatus(status), "enough", "owner", Now);

        stop.Should().Throw<DomainException>().WithMessage($"*{described}; only a live flow run stops.");
    }

    [Theory]
    [InlineData(FlowRunStatus.Running)]
    [InlineData(FlowRunStatus.Paused)]
    public void A_live_flow_run_stops_and_releases_its_parcel(FlowRunStatus status)
    {
        FlowRun flowRun = InStatus(status);

        (FlowRunStopped stopped, ParcelFlowRunReleased released) = FlowRunDecider.Stop(flowRun, "enough", "owner", Now);
        flowRun.Apply(stopped);

        flowRun.IsLive.Should().BeFalse();
        released.ParcelId.Should().Be(flowRun.ParcelId);
        released.FlowRunId.Should().Be(flowRun.Id);
    }

    [Fact]
    public void Recording_against_a_flow_other_than_the_one_the_flow_run_started_under_is_refused()
    {
        Action step = () => FlowRunDecider.StartStep(ShippedFlows.Discovery, Started(), "discover", Now);

        step.Should().Throw<DomainException>().WithMessage("*a flow run keeps the flow it started under.");
    }

    /// <summary>A flow run of <paramref name="flow"/> at its judgment step 'build', launched, paused without an answer and resumed.</summary>
    private static FlowRun ResumedUnanswered(Flow flow, bool launched = true)
    {
        FlowRun flowRun = Started(flow);
        flowRun.Apply(FlowRunDecider.StartStep(flow, flowRun, "build", Now));
        if (launched)
        {
            flowRun.Apply(FlowRunDecider.Spawn(flow, flowRun, "build", "agenthash", Model, Now));
        }

        flowRun.Apply(FlowRunDecider.Pause(flow, flowRun, "no valid answer", "perform it again", "flow-run", Now, Spend));
        flowRun.Apply(FlowRunDecider.Resume(flow, flowRun, "try again", "owner", Now.AddMinutes(1)));
        return flowRun;
    }

    [Fact]
    public void A_respawn_of_a_resumed_unanswered_judgment_counts_one_spawn_and_no_visit_once_per_resume()
    {
        FlowRun flowRun = ResumedUnanswered(Build);
        int spawns = flowRun.Spawns;
        int visits = flowRun.Position.VisitsTo("build");

        flowRun.Apply(FlowRunDecider.Respawn(Build, flowRun, "build", Now.AddMinutes(2)));

        flowRun.Spawns.Should().Be(spawns + 1);
        flowRun.Position.Spawns.Should().Be(spawns + 1);
        flowRun.Position.VisitsTo("build").Should().Be(visits, "the visit is performed again, not entered again");
        flowRun.Steps.Should().HaveCount(2);
        FluentActions.Invoking(() => FlowRunDecider.Respawn(Build, flowRun, "build", Now.AddMinutes(3)))
            .Should().Throw<DomainException>().WithMessage("*already counted a spawn for its latest resume*");

        flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, "build", "agenthash", Model, Now.AddMinutes(3)));
        flowRun.Apply(FlowRunDecider.Pause(Build, flowRun, "no valid answer", "perform it again", "flow-run", Now.AddMinutes(4), Spend));
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, "once more", "owner", Now.AddMinutes(5)));
        flowRun.Apply(FlowRunDecider.Respawn(Build, flowRun, "build", Now.AddMinutes(6)));
        flowRun.Spawns.Should().Be(spawns + 2, "each resume counts its own spawn");
    }

    [Fact]
    public void A_resumed_judgment_that_paused_before_it_launched_uses_its_counted_spawn_rather_than_counting_another()
    {
        Flow capped = Build with { SpawnCap = 1 };
        FlowRun flowRun = ResumedUnanswered(capped, launched: false);

        flowRun.Current.Should().NotBeNull();
        flowRun.Current?.SpawnsAgainOnResume.Should().BeFalse("the spawn counted when the step was entered was never used");
        FluentActions.Invoking(() => FlowRunDecider.Respawn(capped, flowRun, "build", Now.AddMinutes(2)))
            .Should().Throw<DomainException>().WithMessage("*has not launched its agent on the spawn it last counted*");

        flowRun.Apply(FlowRunDecider.Spawn(capped, flowRun, "build", "agenthash", Model, Now.AddMinutes(2)));
        flowRun.Spawns.Should().Be(1, "a launch counts nothing itself");
        flowRun.Current?.SpawnsAgainOnResume.Should().BeFalse("no resume has followed the launch");
    }

    [Fact]
    public void A_respawn_is_refused_at_the_spawn_cap_before_a_resume_and_for_an_action_step()
    {
        Flow capped = Build with { SpawnCap = 1 };
        FluentActions.Invoking(() => FlowRunDecider.Respawn(capped, ResumedUnanswered(capped), "build", Now))
            .Should().Throw<DomainException>()
            .WithMessage("Flow 'build' reached its spawn cap of 1; performing judgment step 'build' again would spawn another agent.");

        FluentActions.Invoking(() => FlowRunDecider.Respawn(Build, AtJudgment(), "build", Now))
            .Should().Throw<DomainException>().WithMessage("*has not been resumed since it was entered*");

        FlowRun atAction = Started();
        atAction.Apply(FlowRunDecider.Pause(Build, atAction, "waiting", "lever", "owner", Now));
        atAction.Apply(FlowRunDecider.Resume(Build, atAction, "go on", "owner", Now));
        FluentActions.Invoking(() => FlowRunDecider.Respawn(Build, atAction, "worktree", Now))
            .Should().Throw<DomainException>().WithMessage("*only a judgment step spawns an agent*");
    }

    [Fact]
    public void A_launch_carries_the_agents_hash_and_model_spec_and_its_session_is_recorded_once_until_the_next_launch()
    {
        FlowRun flowRun = AtJudgment();
        FluentActions.Invoking(() => FlowRunDecider.OpenSession(Build, flowRun, "build", Ids.New(), Now))
            .Should().Throw<DomainException>().WithMessage("*has not recorded the launch of its agent*");

        flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, "build", " agenthash ", Model, Now));
        flowRun.Current?.AgentHash.Should().Be("agenthash");
        flowRun.Current?.Model.Should().Be(Model);
        flowRun.Current?.SessionId.Should().BeNull();
        flowRun.Current?.Answer.Should().BeNull();

        Guid first = Ids.New();
        flowRun.Apply(FlowRunDecider.OpenSession(Build, flowRun, "build", first, Now.AddSeconds(1)));
        flowRun.Current?.SessionId.Should().Be(first);
        flowRun.Spawns.Should().Be(1, "recording a session counts nothing");
        FluentActions.Invoking(() => FlowRunDecider.OpenSession(Build, flowRun, "build", Ids.New(), Now.AddSeconds(2)))
            .Should().Throw<DomainException>().WithMessage("*already holds session*");

        flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, "build", "agenthash", Model, Now.AddSeconds(3)));
        flowRun.Current?.SessionId.Should().BeNull("a launch has no session until the executor returns one");
        flowRun.Spawns.Should().Be(1);
    }

    [Fact]
    public void A_resumes_counted_spawn_clears_the_session_of_the_launch_before_it()
    {
        FlowRun flowRun = ResumedUnanswered(Build);
        flowRun.Apply(new JudgmentSessionOpened(flowRun.Id, "build", Ids.New(), Now.AddMinutes(1)));

        flowRun.Apply(FlowRunDecider.Respawn(Build, flowRun, "build", Now.AddMinutes(2)));

        flowRun.Current?.SessionId.Should().BeNull();
        flowRun.Current?.AgentHash.Should().Be("agenthash", "the visit still names the agent it last launched");
    }

    [Fact]
    public void A_launch_with_no_agent_hash_or_model_spec_is_refused()
    {
        FluentActions.Invoking(() => FlowRunDecider.Spawn(Build, AtJudgment(), "build", " ", Model, Now))
            .Should().Throw<DomainException>().WithMessage("*content hash of the agent it launches*");
        FluentActions.Invoking(() => FlowRunDecider.Spawn(Build, AtJudgment(), "build", "agenthash", Model with { Effort = "" }, Now))
            .Should().Throw<DomainException>().WithMessage("*model spec its agent runs on*");
    }

    [Fact]
    public void A_launch_recorded_before_it_carried_the_agent_replays_with_neither_hash_nor_model_spec()
    {
        FlowRun flowRun = AtJudgment();
        JudgmentSpawned old = JsonSerializer.Deserialize<JudgmentSpawned>(
            $$"""{"FlowRunId":"{{flowRun.Id}}","Step":"build","Agent":"build","SpawnedAt":"2026-11-03T12:00:00+00:00"}""")
            ?? throw new InvalidOperationException("The launch did not read back.");

        flowRun.Apply(old);

        flowRun.Current?.SpawnedAt.Should().Be(Now);
        flowRun.Current?.AgentHash.Should().BeNull();
        flowRun.Current?.Model.Should().BeNull();
    }

    [Fact]
    public void An_answer_from_the_session_a_resume_moved_past_is_refused_even_on_the_same_step_and_visit()
    {
        FlowRun flowRun = AtJudgment();
        Guid held = Launched(flowRun);
        flowRun.Apply(FlowRunDecider.Pause(Build, flowRun, "Hold on.", "resume", "owner", Now));
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, "owner", Now));

        flowRun.Current?.SessionId.Should().Be(held, "a resume does not clear the session it found");
        FluentActions.Invoking(() => FlowRunDecider.Answer(Build, flowRun, "build", "build", held, "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("Step 'build' was resumed after it launched session *; an answer from session * is not recorded on this visit.");
        FluentActions.Invoking(() => FlowRunDecider.Pause(Build, flowRun, "No valid answer.", "/transcripts/1.jsonl", "owner", Now, Spend, held))
            .Should().Throw<DomainException>().WithMessage("Step 'build' was resumed after it launched session *; a pause from session * is not recorded on this visit.");
    }

    [Fact]
    public void An_answer_from_a_session_the_latest_launch_did_not_return_is_refused()
    {
        FlowRun flowRun = AtJudgment();
        Launched(flowRun);

        FluentActions.Invoking(() => FlowRunDecider.Answer(Build, flowRun, "build", "build", Ids.New(), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("Step 'build' expects session *; an answer from session * is not recorded on this visit.");

        FlowRun unopened = AtJudgment();
        unopened.Apply(FlowRunDecider.Spawn(Build, unopened, "build", "agenthash", Model, Now));
        FluentActions.Invoking(() => FlowRunDecider.Answer(Build, unopened, "build", "build", Ids.New(), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("Step 'build' holds no session for its latest launch; an answer from session * is not recorded on this visit.");
    }

    [Theory]
    [InlineData(FlowRunStatus.Running)]
    [InlineData(FlowRunStatus.Paused)]
    [InlineData(FlowRunStatus.Stopped)]
    public void A_dropped_judgments_usage_counts_once_in_the_cost_and_leaves_the_rest_of_the_flow_run_as_it_was(FlowRunStatus status)
    {
        FlowRun flowRun = AtJudgment();
        Guid session = Launched(flowRun);
        var usage = new Measurement(20, 2.25m, 2, TimeSpan.FromSeconds(2));
        switch (status)
        {
            case FlowRunStatus.Paused:
                flowRun.Apply(FlowRunDecider.Pause(Build, flowRun, "Hold on.", "resume", "owner", Now, new Measurement(1, 0.5m, 1, TimeSpan.Zero)));
                break;
            case FlowRunStatus.Stopped:
                flowRun.Apply(FlowRunDecider.Stop(flowRun, "Not wanted.", "owner", Now).Stopped);
                break;
        }

        decimal before = flowRun.Cost;
        string? reason = flowRun.PauseReason;
        string? lever = flowRun.Lever;
        Measurement? pauseUsage = flowRun.PauseUsage;
        FlowRunStepEntry? visit = flowRun.Current;

        JudgmentUsageRecorded recorded = FlowRunDecider.RecordUsage(Build, flowRun, "build", "build", session, usage, Now.AddMinutes(1));
        flowRun.Apply(recorded);

        recorded.Usage.Should().Be(usage);
        flowRun.Cost.Should().Be(before + usage.CostUsd);
        flowRun.Status.Should().Be(status);
        flowRun.PauseReason.Should().Be(reason);
        flowRun.Lever.Should().Be(lever);
        flowRun.PauseUsage.Should().Be(pauseUsage);
        flowRun.Current.Should().Be(visit);
    }

    [Fact]
    public void A_dropped_judgments_usage_is_refused_on_an_ended_flow_run_another_step_or_an_action()
    {
        FlowRun ended = InStatus(FlowRunStatus.Ended);
        FluentActions.Invoking(() => FlowRunDecider.RecordUsage(Build, ended, "worktree", "build", Ids.New(), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("*has ended; a judgment's usage is recorded on a running, paused, or stopped flow run.");

        FluentActions.Invoking(() => FlowRunDecider.RecordUsage(Build, AtJudgment(), "worktree", "build", Ids.New(), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("*is at step 'build'; a judgment's usage is recorded only for the current step, not 'worktree'.");
        FluentActions.Invoking(() => FlowRunDecider.RecordUsage(Build, Started(), "worktree", "worktree", Ids.New(), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("Step 'worktree' is an action step; only a judgment step reports usage.");
        FluentActions.Invoking(() => FlowRunDecider.RecordUsage(Build, AtJudgment(), "build", "build", Ids.New(), Spend with { Tokens = -1 }, Now))
            .Should().Throw<DomainException>().WithMessage("A measurement is never negative*");
    }
}
