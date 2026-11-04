using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.Flows.Events;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class FlowDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Contour Build = ShippedContours.Build;
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
    /// visit expects: the flow's current judgment is launched and its session opened first when it
    /// holds none.
    /// </summary>
    private static JudgmentAnswered AnswerAt(Flow flow, string step) =>
        FlowDecider.Answer(Build, flow, step, "build", Launched(flow), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now);

    /// <summary>The session the flow's current visit expects, launching it and opening one when an unrecorded running judgment holds none.</summary>
    private static Guid Launched(Flow flow)
    {
        if (flow is { Status: FlowStatus.Running, Current: { Kind: StepKind.Judgment, IsRecorded: false, SessionId: null } current })
        {
            flow.Apply(FlowDecider.Spawn(Build, flow, current.Step, "agenthash", Model, Now));
            flow.Apply(FlowDecider.OpenSession(Build, flow, current.Step, Ids.New(), Now));
        }

        return flow.Current?.SessionId ?? Ids.New();
    }

    /// <summary>A build flow started on a fresh parcel, standing at its first step, the worktree action.</summary>
    private static Flow Started(Contour? contour = null)
    {
        (FlowStarted started, _) = FlowDecider.Start(contour ?? Build, 1, ParcelOf(), "owner", Now);
        return Flow.Create(started);
    }

    /// <summary>A build flow at its judgment step, 'build'.</summary>
    private static Flow AtJudgment()
    {
        Flow flow = Started();
        flow.Apply(FlowDecider.StartStep(Build, flow, "build", Now));
        return flow;
    }

    private static Flow InStatus(FlowStatus status)
    {
        Flow flow = Started();
        switch (status)
        {
            case FlowStatus.Paused:
                flow.Apply(FlowDecider.Pause(Build, flow, "waiting", "answer the question", "owner", Now));
                break;
            case FlowStatus.Ended:
                flow.Apply(FlowDecider.End(Build, flow, Build.Outcome, "owner", Now).Ended);
                break;
            case FlowStatus.Stopped:
                flow.Apply(FlowDecider.Stop(flow, "no longer wanted", "owner", Now).Stopped);
                break;
        }

        return flow;
    }

    [Fact]
    public void Replaying_a_start_and_two_step_starts_yields_the_interpreters_position()
    {
        Flow flow = Started();
        flow.Position.Should().Be(Interpreter.Start(Build), "a started flow has already visited its first step once");

        flow.Apply(FlowDecider.StartStep(Build, flow, "build", Now));
        flow.Apply(FlowDecider.StartStep(Build, flow, "gates", Now));

        FlowPosition expected = Interpreter.Apply(
            Build, Interpreter.Apply(Build, Interpreter.Start(Build), new FlowMove.GoTo("build")), new FlowMove.GoTo("gates"));
        flow.Position.Should().Be(expected);
        flow.Position.Step.Should().Be("gates");
        flow.Position.Spawns.Should().Be(1, "only the judgment step 'build' spawned an agent");
        flow.Position.VisitsTo("worktree").Should().Be(1);
        flow.Position.VisitsTo("build").Should().Be(1);
        flow.Position.VisitsTo("gates").Should().Be(1);
    }

    [Fact]
    public void A_flow_whose_first_step_is_a_judgment_counts_one_spawn_at_start()
    {
        Flow flow = Started(ShippedContours.ReviewOnAssignment);

        flow.Position.Should().Be(Interpreter.Start(ShippedContours.ReviewOnAssignment));
        flow.Position.Spawns.Should().Be(1);
    }

    [Fact]
    public void Replay_reads_the_events_alone_and_never_the_contour()
    {
        Flow flow = Flow.Create(new FlowStarted(
            Ids.New(), Ids.New(), "any", 1, "h", "owner", "ask", StepKind.Judgment, "an-agent", Now));
        flow.Apply(new StepStarted(flow.Id, "do", StepKind.Action, "an-action", Now));
        flow.Apply(new StepStarted(flow.Id, "ask", StepKind.Judgment, "an-agent", Now));

        flow.Position.Step.Should().Be("ask");
        flow.Position.VisitsTo("ask").Should().Be(2);
        flow.Position.Spawns.Should().Be(2);
    }

    [Fact]
    public void A_start_attaches_the_flow_to_its_parcel_and_records_the_contour_and_first_step()
    {
        Parcel parcel = ParcelOf();
        (FlowStarted started, ParcelFlowAttached attached) = FlowDecider.Start(Build, 3, parcel, "owner", Now);

        started.ParcelId.Should().Be(parcel.Id);
        started.ContourName.Should().Be("build");
        started.ContourVersion.Should().Be(3);
        started.ContourHash.Should().Be(Build.Hash);
        started.FirstStep.Should().Be("worktree");
        started.FirstStepKind.Should().Be(StepKind.Action);
        started.FirstStepPerformer.Should().Be(SoftwareActions.Worktree);
        attached.FlowId.Should().Be(started.FlowId);
        attached.ParcelId.Should().Be(parcel.Id);
    }

    [Fact]
    public void A_start_on_a_closed_parcel_is_refused()
    {
        Parcel parcel = ParcelOf();
        parcel.Apply(new ParcelClosed(parcel.Id, "build", "closed", "abandoned", "Not needed.", "owner", Now));

        Action start = () => FlowDecider.Start(Build, 1, parcel, "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("*is closed (abandoned); a flow starts only on an open parcel*");
    }

    [Fact]
    public void A_start_on_a_parcel_whose_type_the_contour_does_not_accept_is_refused()
    {
        Action start = () => FlowDecider.Start(Build, 1, ParcelOf("idea"), "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("Contour 'build' accepts parcels of type task, not 'idea'.");
    }

    [Fact]
    public void A_start_on_a_parcel_that_already_has_a_live_flow_is_refused()
    {
        Parcel parcel = ParcelOf();
        parcel.Apply(new ParcelFlowAttached(parcel.Id, Ids.New(), Now));

        Action start = () => FlowDecider.Start(Build, 1, parcel, "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("A parcel has at most one live flow;*");
    }

    public static TheoryData<FlowStatus, string> NotRunning()
    {
        var data = new TheoryData<FlowStatus, string>();
        foreach (FlowStatus status in new[] { FlowStatus.Paused, FlowStatus.Ended, FlowStatus.Stopped })
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
    public void Recording_on_a_flow_that_is_not_running_is_refused(FlowStatus status, string intent)
    {
        Flow flow = InStatus(status);

        Action record = intent switch
        {
            "step" => () => FlowDecider.StartStep(Build, flow, "build", Now),
            "answer" => () => FlowDecider.Answer(Build, flow, "worktree", "build", Ids.New(), "agenthash", Model, Json("{}"), Spend, Now),
            "result" => () => FlowDecider.Complete(Build, flow, "worktree", SoftwareActions.Worktree, Json("{}"), Now),
            "pause" => () => FlowDecider.Pause(Build, flow, "why", "lever", "owner", Now),
            "end" => () => FlowDecider.End(Build, flow, Build.Outcome, "owner", Now),
            _ => throw new ArgumentOutOfRangeException(nameof(intent)),
        };

        record.Should().Throw<DomainException>().WithMessage("*; only a running flow can *");
    }

    [Fact]
    public void Starting_a_step_the_contour_does_not_have_is_refused()
    {
        Action step = () => FlowDecider.StartStep(Build, Started(), "deploy", Now);

        step.Should().Throw<DomainException>().WithMessage("Contour 'build' has no step 'deploy'; a flow enters only a step its contour has.");
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
        Action result = () => FlowDecider.Complete(Build, Started(), "gates", SoftwareActions.Gates, Json("{}"), Now);

        result.Should().Throw<DomainException>().WithMessage("*is at step 'worktree'; a result is recorded only for the current step, not 'gates'.");
    }

    [Fact]
    public void An_answer_on_an_action_step_is_refused()
    {
        Action answer = () => FlowDecider.Answer(Build, Started(), "worktree", "build", Ids.New(), "agenthash", Model, Json("{}"), Spend, Now);

        answer.Should().Throw<DomainException>().WithMessage("Step 'worktree' is an action step; it takes a result, not an answer.");
    }

    [Fact]
    public void A_result_on_a_judgment_step_is_refused()
    {
        Action result = () => FlowDecider.Complete(Build, AtJudgment(), "build", "build", Json("{}"), Now);

        result.Should().Throw<DomainException>().WithMessage("Step 'build' is a judgment step; it takes an answer, not a result.");
    }

    [Fact]
    public void An_answer_records_the_agent_its_hash_the_model_the_document_and_the_measurement()
    {
        Flow flow = AtJudgment();
        JudgmentAnswered answered = AnswerAt(flow, "build");
        flow.Apply(answered);

        answered.Agent.Should().Be("build");
        answered.AgentHash.Should().Be("agenthash");
        answered.Model.Should().Be(Model);
        answered.Answer.GetProperty("verdict").GetString().Should().Be("done");
        answered.Measurement.Should().Be(Spend);
        FlowStepEntry? current = flow.Current;
        current.Should().NotBeNull();
        current.Answer.HasValue.Should().BeTrue();
        current.Answer.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("done");
        current.Measurement.Should().Be(Spend);
    }

    [Fact]
    public void A_second_answer_on_the_same_visit_is_refused()
    {
        Flow flow = AtJudgment();
        flow.Apply(AnswerAt(flow, "build"));

        Action again = () => AnswerAt(flow, "build");

        again.Should().Throw<DomainException>().WithMessage("Step 'build' already holds what this visit recorded*");
    }

    [Fact]
    public void An_answer_from_an_agent_the_step_does_not_name_is_refused()
    {
        Action answer = () => FlowDecider.Answer(Build, AtJudgment(), "build", "fix", Ids.New(), "agenthash", Model, Json("{}"), Spend, Now);

        answer.Should().Throw<DomainException>().WithMessage("Step 'build' names the agent 'build'; an answer from 'fix' is not its answer.");
    }

    [Fact]
    public void A_negative_measurement_is_refused()
    {
        Action answer = () => FlowDecider.Answer(
            Build, AtJudgment(), "build", "build", Ids.New(), "agenthash", Model, Json("{}"), Spend with { CostUsd = -1m }, Now);

        answer.Should().Throw<DomainException>().WithMessage("A measurement is never negative*");
    }

    [Theory]
    [InlineData("", "answer the question", "A pause records why the flow stopped; give a reason.")]
    [InlineData("waiting", " ", "A pause records the lever a person acts on to resume the flow; give a lever.")]
    public void A_pause_with_an_empty_reason_or_lever_is_refused(string reason, string lever, string message)
    {
        Action pause = () => FlowDecider.Pause(Build, Started(), reason, lever, "owner", Now);

        pause.Should().Throw<DomainException>().WithMessage(message);
    }

    [Fact]
    public void An_end_with_an_outcome_other_than_the_contours_is_refused()
    {
        Action end = () => FlowDecider.End(Build, Started(), "merged", "owner", Now);

        end.Should().Throw<DomainException>().WithMessage("Contour 'build' ends with its declared outcome 'pr-opened', not 'merged'.");
    }

    [Theory]
    [InlineData(FlowStatus.Running)]
    [InlineData(FlowStatus.Ended)]
    [InlineData(FlowStatus.Stopped)]
    public void Resuming_a_flow_that_is_not_paused_is_refused(FlowStatus status)
    {
        Action resume = () => FlowDecider.Resume(Build, InStatus(status), "go on", "owner", Now);

        resume.Should().Throw<DomainException>().WithMessage("*; only a paused flow resumes.");
    }

    [Fact]
    public void A_paused_flow_resumes_and_runs_again()
    {
        Flow flow = InStatus(FlowStatus.Paused);
        flow.IsLive.Should().BeTrue("a paused flow is still live");

        flow.Apply(FlowDecider.Resume(Build, flow, "answered", "owner", Now));

        flow.Status.Should().Be(FlowStatus.Running);
        flow.Lever.Should().BeNull();
    }

    [Theory]
    [InlineData(FlowStatus.Ended, "has ended")]
    [InlineData(FlowStatus.Stopped, "has been stopped")]
    public void Stopping_a_flow_that_has_ended_or_stopped_is_refused(FlowStatus status, string described)
    {
        Action stop = () => FlowDecider.Stop(InStatus(status), "enough", "owner", Now);

        stop.Should().Throw<DomainException>().WithMessage($"*{described}; only a live flow stops.");
    }

    [Theory]
    [InlineData(FlowStatus.Running)]
    [InlineData(FlowStatus.Paused)]
    public void A_live_flow_stops_and_releases_its_parcel(FlowStatus status)
    {
        Flow flow = InStatus(status);

        (FlowStopped stopped, ParcelFlowReleased released) = FlowDecider.Stop(flow, "enough", "owner", Now);
        flow.Apply(stopped);

        flow.IsLive.Should().BeFalse();
        released.ParcelId.Should().Be(flow.ParcelId);
        released.FlowId.Should().Be(flow.Id);
    }

    [Fact]
    public void Recording_against_a_contour_other_than_the_one_the_flow_started_under_is_refused()
    {
        Action step = () => FlowDecider.StartStep(ShippedContours.Discovery, Started(), "discover", Now);

        step.Should().Throw<DomainException>().WithMessage("*a flow keeps the contour it started under.");
    }

    /// <summary>A flow of <paramref name="contour"/> at its judgment step 'build', launched, paused without an answer and resumed.</summary>
    private static Flow ResumedUnanswered(Contour contour, bool launched = true)
    {
        Flow flow = Started(contour);
        flow.Apply(FlowDecider.StartStep(contour, flow, "build", Now));
        if (launched)
        {
            flow.Apply(FlowDecider.Spawn(contour, flow, "build", "agenthash", Model, Now));
        }

        flow.Apply(FlowDecider.Pause(contour, flow, "no valid answer", "perform it again", "flow", Now, Spend));
        flow.Apply(FlowDecider.Resume(contour, flow, "try again", "owner", Now.AddMinutes(1)));
        return flow;
    }

    [Fact]
    public void A_respawn_of_a_resumed_unanswered_judgment_counts_one_spawn_and_no_visit_once_per_resume()
    {
        Flow flow = ResumedUnanswered(Build);
        int spawns = flow.Spawns;
        int visits = flow.Position.VisitsTo("build");

        flow.Apply(FlowDecider.Respawn(Build, flow, "build", Now.AddMinutes(2)));

        flow.Spawns.Should().Be(spawns + 1);
        flow.Position.Spawns.Should().Be(spawns + 1);
        flow.Position.VisitsTo("build").Should().Be(visits, "the visit is performed again, not entered again");
        flow.Steps.Should().HaveCount(2);
        FluentActions.Invoking(() => FlowDecider.Respawn(Build, flow, "build", Now.AddMinutes(3)))
            .Should().Throw<DomainException>().WithMessage("*already counted a spawn for its latest resume*");

        flow.Apply(FlowDecider.Spawn(Build, flow, "build", "agenthash", Model, Now.AddMinutes(3)));
        flow.Apply(FlowDecider.Pause(Build, flow, "no valid answer", "perform it again", "flow", Now.AddMinutes(4), Spend));
        flow.Apply(FlowDecider.Resume(Build, flow, "once more", "owner", Now.AddMinutes(5)));
        flow.Apply(FlowDecider.Respawn(Build, flow, "build", Now.AddMinutes(6)));
        flow.Spawns.Should().Be(spawns + 2, "each resume counts its own spawn");
    }

    [Fact]
    public void A_resumed_judgment_that_paused_before_it_launched_uses_its_counted_spawn_rather_than_counting_another()
    {
        Contour capped = Build with { SpawnCap = 1 };
        Flow flow = ResumedUnanswered(capped, launched: false);

        flow.Current.Should().NotBeNull();
        flow.Current?.SpawnsAgainOnResume.Should().BeFalse("the spawn counted when the step was entered was never used");
        FluentActions.Invoking(() => FlowDecider.Respawn(capped, flow, "build", Now.AddMinutes(2)))
            .Should().Throw<DomainException>().WithMessage("*has not launched its agent on the spawn it last counted*");

        flow.Apply(FlowDecider.Spawn(capped, flow, "build", "agenthash", Model, Now.AddMinutes(2)));
        flow.Spawns.Should().Be(1, "a launch counts nothing itself");
        flow.Current?.SpawnsAgainOnResume.Should().BeFalse("no resume has followed the launch");
    }

    [Fact]
    public void A_respawn_is_refused_at_the_spawn_cap_before_a_resume_and_for_an_action_step()
    {
        Contour capped = Build with { SpawnCap = 1 };
        FluentActions.Invoking(() => FlowDecider.Respawn(capped, ResumedUnanswered(capped), "build", Now))
            .Should().Throw<DomainException>()
            .WithMessage("Contour 'build' reached its spawn cap of 1; performing judgment step 'build' again would spawn another agent.");

        FluentActions.Invoking(() => FlowDecider.Respawn(Build, AtJudgment(), "build", Now))
            .Should().Throw<DomainException>().WithMessage("*has not been resumed since it was entered*");

        Flow atAction = Started();
        atAction.Apply(FlowDecider.Pause(Build, atAction, "waiting", "lever", "owner", Now));
        atAction.Apply(FlowDecider.Resume(Build, atAction, "go on", "owner", Now));
        FluentActions.Invoking(() => FlowDecider.Respawn(Build, atAction, "worktree", Now))
            .Should().Throw<DomainException>().WithMessage("*only a judgment step spawns an agent*");
    }

    [Fact]
    public void A_launch_carries_the_agents_hash_and_model_spec_and_its_session_is_recorded_once_until_the_next_launch()
    {
        Flow flow = AtJudgment();
        FluentActions.Invoking(() => FlowDecider.OpenSession(Build, flow, "build", Ids.New(), Now))
            .Should().Throw<DomainException>().WithMessage("*has not recorded the launch of its agent*");

        flow.Apply(FlowDecider.Spawn(Build, flow, "build", " agenthash ", Model, Now));
        flow.Current?.AgentHash.Should().Be("agenthash");
        flow.Current?.Model.Should().Be(Model);
        flow.Current?.SessionId.Should().BeNull();
        flow.Current?.Answer.Should().BeNull();

        Guid first = Ids.New();
        flow.Apply(FlowDecider.OpenSession(Build, flow, "build", first, Now.AddSeconds(1)));
        flow.Current?.SessionId.Should().Be(first);
        flow.Spawns.Should().Be(1, "recording a session counts nothing");
        FluentActions.Invoking(() => FlowDecider.OpenSession(Build, flow, "build", Ids.New(), Now.AddSeconds(2)))
            .Should().Throw<DomainException>().WithMessage("*already holds session*");

        flow.Apply(FlowDecider.Spawn(Build, flow, "build", "agenthash", Model, Now.AddSeconds(3)));
        flow.Current?.SessionId.Should().BeNull("a launch has no session until the executor returns one");
        flow.Spawns.Should().Be(1);
    }

    [Fact]
    public void A_resumes_counted_spawn_clears_the_session_of_the_launch_before_it()
    {
        Flow flow = ResumedUnanswered(Build);
        flow.Apply(new JudgmentSessionOpened(flow.Id, "build", Ids.New(), Now.AddMinutes(1)));

        flow.Apply(FlowDecider.Respawn(Build, flow, "build", Now.AddMinutes(2)));

        flow.Current?.SessionId.Should().BeNull();
        flow.Current?.AgentHash.Should().Be("agenthash", "the visit still names the agent it last launched");
    }

    [Fact]
    public void A_launch_with_no_agent_hash_or_model_spec_is_refused()
    {
        FluentActions.Invoking(() => FlowDecider.Spawn(Build, AtJudgment(), "build", " ", Model, Now))
            .Should().Throw<DomainException>().WithMessage("*content hash of the agent it launches*");
        FluentActions.Invoking(() => FlowDecider.Spawn(Build, AtJudgment(), "build", "agenthash", Model with { Effort = "" }, Now))
            .Should().Throw<DomainException>().WithMessage("*model spec its agent runs on*");
    }

    [Fact]
    public void A_launch_recorded_before_it_carried_the_agent_replays_with_neither_hash_nor_model_spec()
    {
        Flow flow = AtJudgment();
        JudgmentSpawned old = JsonSerializer.Deserialize<JudgmentSpawned>(
            $$"""{"FlowId":"{{flow.Id}}","Step":"build","Agent":"build","SpawnedAt":"2026-11-03T12:00:00+00:00"}""")
            ?? throw new InvalidOperationException("The launch did not read back.");

        flow.Apply(old);

        flow.Current?.SpawnedAt.Should().Be(Now);
        flow.Current?.AgentHash.Should().BeNull();
        flow.Current?.Model.Should().BeNull();
    }

    [Fact]
    public void An_answer_from_the_session_a_resume_moved_past_is_refused_even_on_the_same_step_and_visit()
    {
        Flow flow = AtJudgment();
        Guid held = Launched(flow);
        flow.Apply(FlowDecider.Pause(Build, flow, "Hold on.", "resume", "owner", Now));
        flow.Apply(FlowDecider.Resume(Build, flow, null, "owner", Now));

        flow.Current?.SessionId.Should().Be(held, "a resume does not clear the session it found");
        FluentActions.Invoking(() => FlowDecider.Answer(Build, flow, "build", "build", held, "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("Step 'build' was resumed after it launched session *; an answer from session * is not recorded on this visit.");
        FluentActions.Invoking(() => FlowDecider.Pause(Build, flow, "No valid answer.", "/transcripts/1.jsonl", "owner", Now, Spend, held))
            .Should().Throw<DomainException>().WithMessage("Step 'build' was resumed after it launched session *; a pause from session * is not recorded on this visit.");
    }

    [Fact]
    public void An_answer_from_a_session_the_latest_launch_did_not_return_is_refused()
    {
        Flow flow = AtJudgment();
        Launched(flow);

        FluentActions.Invoking(() => FlowDecider.Answer(Build, flow, "build", "build", Ids.New(), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("Step 'build' expects session *; an answer from session * is not recorded on this visit.");

        Flow unopened = AtJudgment();
        unopened.Apply(FlowDecider.Spawn(Build, unopened, "build", "agenthash", Model, Now));
        FluentActions.Invoking(() => FlowDecider.Answer(Build, unopened, "build", "build", Ids.New(), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("Step 'build' holds no session for its latest launch; an answer from session * is not recorded on this visit.");
    }

    [Theory]
    [InlineData(FlowStatus.Running)]
    [InlineData(FlowStatus.Paused)]
    [InlineData(FlowStatus.Stopped)]
    public void A_dropped_judgments_usage_counts_once_in_the_cost_and_leaves_the_rest_of_the_flow_as_it_was(FlowStatus status)
    {
        Flow flow = AtJudgment();
        Guid session = Launched(flow);
        var usage = new Measurement(20, 2.25m, 2, TimeSpan.FromSeconds(2));
        switch (status)
        {
            case FlowStatus.Paused:
                flow.Apply(FlowDecider.Pause(Build, flow, "Hold on.", "resume", "owner", Now, new Measurement(1, 0.5m, 1, TimeSpan.Zero)));
                break;
            case FlowStatus.Stopped:
                flow.Apply(FlowDecider.Stop(flow, "Not wanted.", "owner", Now).Stopped);
                break;
        }

        decimal before = flow.Cost;
        string? reason = flow.PauseReason;
        string? lever = flow.Lever;
        Measurement? pauseUsage = flow.PauseUsage;
        FlowStepEntry? visit = flow.Current;

        JudgmentUsageRecorded recorded = FlowDecider.RecordUsage(Build, flow, "build", "build", session, usage, Now.AddMinutes(1));
        flow.Apply(recorded);

        recorded.Usage.Should().Be(usage);
        flow.Cost.Should().Be(before + usage.CostUsd);
        flow.Status.Should().Be(status);
        flow.PauseReason.Should().Be(reason);
        flow.Lever.Should().Be(lever);
        flow.PauseUsage.Should().Be(pauseUsage);
        flow.Current.Should().Be(visit);
    }

    [Fact]
    public void A_dropped_judgments_usage_is_refused_on_an_ended_flow_another_step_or_an_action()
    {
        Flow ended = InStatus(FlowStatus.Ended);
        FluentActions.Invoking(() => FlowDecider.RecordUsage(Build, ended, "worktree", "build", Ids.New(), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("*has ended; a judgment's usage is recorded on a running, paused, or stopped flow.");

        FluentActions.Invoking(() => FlowDecider.RecordUsage(Build, AtJudgment(), "worktree", "build", Ids.New(), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("*is at step 'build'; a judgment's usage is recorded only for the current step, not 'worktree'.");
        FluentActions.Invoking(() => FlowDecider.RecordUsage(Build, Started(), "worktree", "worktree", Ids.New(), Spend, Now))
            .Should().Throw<DomainException>().WithMessage("Step 'worktree' is an action step; only a judgment step reports usage.");
        FluentActions.Invoking(() => FlowDecider.RecordUsage(Build, AtJudgment(), "build", "build", Ids.New(), Spend with { Tokens = -1 }, Now))
            .Should().Throw<DomainException>().WithMessage("A measurement is never negative*");
    }
}
