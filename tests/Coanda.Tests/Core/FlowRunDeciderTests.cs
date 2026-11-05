using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Modules.Software;

namespace Coanda.Tests.Core;

public sealed class FlowRunDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Flow Build = SoftwareFlows.Build;
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
    /// visit expects: the flow run's current agent step is launched and its session opened first when it
    /// holds none.
    /// </summary>
    private static AgentStepAnswered AnswerAt(FlowRun flowRun, string step) =>
        FlowRunDecider.Answer(Build, flowRun, step, "build", Launched(flowRun), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now);

    /// <summary>The session the flow run's current visit expects, launching it and opening one when an unrecorded running agent step holds none.</summary>
    private static Guid Launched(FlowRun flowRun)
    {
        if (flowRun is { Status: FlowRunStatus.Running, Current: { Kind: StepKind.AgentStep, IsRecorded: false, SessionId: null } current })
        {
            flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, current.Step, "agenthash", Model, Now));
            flowRun.Apply(FlowRunDecider.OpenSession(Build, flowRun, current.Step, Ids.New(), Now));
        }

        return flowRun.Current?.SessionId ?? Ids.New();
    }

    /// <summary>A build flow run started on a fresh parcel, standing at its first step, the worktree system step.</summary>
    private static FlowRun Started(Flow? flow = null)
    {
        (FlowRunStarted started, _) = FlowRunDecider.Start(flow ?? Build, 1, ParcelOf(), "owner", Now);
        return FlowRun.Create(started);
    }

    /// <summary>A build flow run at its agent step, 'build'.</summary>
    private static FlowRun AtAgentStep()
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
        // a started flow run has already visited its first step once
        Assert.Equal(Interpreter.Start(Build), flowRun.Position);

        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "build", Now));
        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "gates", Now));

        FlowRunPosition expected = Interpreter.Apply(
            Build, Interpreter.Apply(Build, Interpreter.Start(Build), new FlowRunMove.GoTo("build")), new FlowRunMove.GoTo("gates"));
        Assert.Equal(expected, flowRun.Position);
        Assert.Equal("gates", flowRun.Position.Step);
        // only the agent step 'build' spawned an agent
        Assert.Equal(1, flowRun.Position.Spawns);
        Assert.Equal(1, flowRun.Position.VisitsTo("worktree"));
        Assert.Equal(1, flowRun.Position.VisitsTo("build"));
        Assert.Equal(1, flowRun.Position.VisitsTo("gates"));
    }

    [Fact]
    public void A_flow_run_whose_first_step_is_an_agent_step_counts_one_spawn_at_start()
    {
        FlowRun flowRun = Started(SoftwareFlows.ReviewOnAssignment);

        Assert.Equal(Interpreter.Start(SoftwareFlows.ReviewOnAssignment), flowRun.Position);
        Assert.Equal(1, flowRun.Position.Spawns);
    }

    [Fact]
    public void Replay_reads_the_events_alone_and_never_the_flow()
    {
        FlowRun flowRun = FlowRun.Create(new FlowRunStarted(
            Ids.New(), Ids.New(), "any", 1, "h", "owner", "ask", StepKind.AgentStep, "an-agent", Now));
        flowRun.Apply(new StepStarted(flowRun.Id, "do", StepKind.SystemStep, "a-system-step", Now));
        flowRun.Apply(new StepStarted(flowRun.Id, "ask", StepKind.AgentStep, "an-agent", Now));

        Assert.Equal("ask", flowRun.Position.Step);
        Assert.Equal(2, flowRun.Position.VisitsTo("ask"));
        Assert.Equal(2, flowRun.Position.Spawns);
    }

    [Fact]
    public void A_start_attaches_the_flow_run_to_its_parcel_and_records_the_flow_and_first_step()
    {
        Parcel parcel = ParcelOf();
        (FlowRunStarted started, ParcelFlowRunAttached attached) = FlowRunDecider.Start(Build, 3, parcel, "owner", Now);

        Assert.Equal(parcel.Id, started.ParcelId);
        Assert.Equal("build", started.FlowName);
        Assert.Equal(3, started.FlowVersion);
        Assert.Equal(Build.Hash, started.FlowHash);
        Assert.Equal("worktree", started.FirstStep);
        Assert.Equal(StepKind.SystemStep, started.FirstStepKind);
        Assert.Equal(SoftwareSteps.Worktree, started.FirstStepPerformer);
        Assert.Equal(started.FlowRunId, attached.FlowRunId);
        Assert.Equal(parcel.Id, attached.ParcelId);
    }

    [Fact]
    public void A_start_on_a_closed_parcel_is_refused()
    {
        Parcel parcel = ParcelOf();
        parcel.Apply(new ParcelClosed(parcel.Id, "build", "closed", "abandoned", "Not needed.", "owner", Now));

        Action start = () => FlowRunDecider.Start(Build, 1, parcel, "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(start);
        Assert.Contains("is closed (abandoned); a flow run starts only on an open parcel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_on_a_parcel_whose_type_the_flow_does_not_accept_is_refused()
    {
        Action start = () => FlowRunDecider.Start(Build, 1, ParcelOf("idea"), "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(start);
        Assert.Equal("Flow 'build' accepts parcels of type task, not 'idea'.", exception.Message);
    }

    [Fact]
    public void A_start_on_a_parcel_that_already_has_a_live_flow_run_is_refused()
    {
        Parcel parcel = ParcelOf();
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, Ids.New(), Now));

        Action start = () => FlowRunDecider.Start(Build, 1, parcel, "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(start);
        Assert.StartsWith("A parcel has at most one live flow run;", exception.Message, StringComparison.Ordinal);
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
            "result" => () => FlowRunDecider.Complete(Build, flowRun, "worktree", SoftwareSteps.Worktree, Json("{}"), Now),
            "pause" => () => FlowRunDecider.Pause(Build, flowRun, "why", "lever", "owner", Now),
            "end" => () => FlowRunDecider.End(Build, flowRun, Build.Outcome, "owner", Now),
            _ => throw new ArgumentOutOfRangeException(nameof(intent)),
        };

        DomainException exception = Assert.Throws<DomainException>(record);
        Assert.Contains("; only a running flow run can ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Starting_a_step_the_flow_does_not_have_is_refused()
    {
        Action step = () => FlowRunDecider.StartStep(Build, Started(), "deploy", Now);

        DomainException exception = Assert.Throws<DomainException>(step);
        Assert.Equal("Flow 'build' has no step 'deploy'; a flow run enters only a step its flow has.", exception.Message);
    }

    [Fact]
    public void An_answer_for_a_step_other_than_the_current_one_is_refused()
    {
        Action answer = () => AnswerAt(AtAgentStep(), "fix");

        DomainException exception = Assert.Throws<DomainException>(answer);
        Assert.EndsWith("is at step 'build'; an answer is recorded only for the current step, not 'fix'.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_for_a_step_other_than_the_current_one_is_refused()
    {
        Action result = () => FlowRunDecider.Complete(Build, Started(), "gates", SoftwareSteps.Gates, Json("{}"), Now);

        DomainException exception = Assert.Throws<DomainException>(result);
        Assert.EndsWith("is at step 'worktree'; a result is recorded only for the current step, not 'gates'.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_on_a_system_step_is_refused()
    {
        Action answer = () => FlowRunDecider.Answer(Build, Started(), "worktree", "build", Ids.New(), "agenthash", Model, Json("{}"), Spend, Now);

        DomainException exception = Assert.Throws<DomainException>(answer);
        Assert.Equal("Step 'worktree' is a system step; it takes a result, not an answer.", exception.Message);
    }

    [Fact]
    public void A_result_on_an_agent_step_is_refused()
    {
        Action result = () => FlowRunDecider.Complete(Build, AtAgentStep(), "build", "build", Json("{}"), Now);

        DomainException exception = Assert.Throws<DomainException>(result);
        Assert.Equal("Step 'build' is an agent step; it takes an answer, not a result.", exception.Message);
    }

    [Fact]
    public void An_answer_records_the_agent_its_hash_the_model_the_document_and_the_measurement()
    {
        FlowRun flowRun = AtAgentStep();
        AgentStepAnswered answered = AnswerAt(flowRun, "build");
        flowRun.Apply(answered);

        Assert.Equal("build", answered.Agent);
        Assert.Equal("agenthash", answered.AgentHash);
        Assert.Equal(Model, answered.Model);
        Assert.Equal("done", answered.Answer.GetProperty("verdict").GetString());
        Assert.Equal(Spend, answered.Measurement);
        FlowRunStepEntry? current = flowRun.Current;
        Assert.NotNull(current);
        Assert.True(current.Answer.HasValue);
        Assert.Equal("done", current.Answer.GetValueOrDefault().GetProperty("verdict").GetString());
        Assert.Equal(Spend, current.Measurement);
    }

    [Fact]
    public void A_second_answer_on_the_same_visit_is_refused()
    {
        FlowRun flowRun = AtAgentStep();
        flowRun.Apply(AnswerAt(flowRun, "build"));

        Action again = () => AnswerAt(flowRun, "build");

        DomainException exception = Assert.Throws<DomainException>(again);
        Assert.StartsWith("Step 'build' already holds what this visit recorded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_from_an_agent_the_step_does_not_name_is_refused()
    {
        Action answer = () => FlowRunDecider.Answer(Build, AtAgentStep(), "build", "fix", Ids.New(), "agenthash", Model, Json("{}"), Spend, Now);

        DomainException exception = Assert.Throws<DomainException>(answer);
        Assert.Equal("Step 'build' names the agent 'build'; an answer from 'fix' is not its answer.", exception.Message);
    }

    [Fact]
    public void A_negative_measurement_is_refused()
    {
        Action answer = () => FlowRunDecider.Answer(
            Build, AtAgentStep(), "build", "build", Ids.New(), "agenthash", Model, Json("{}"), Spend with { CostUsd = -1m }, Now);

        DomainException exception = Assert.Throws<DomainException>(answer);
        Assert.StartsWith("A measurement is never negative", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "answer the question", "A pause records why the flow run stopped; give a reason.")]
    [InlineData("waiting", " ", "A pause records the lever a person acts on to resume the flow run; give a lever.")]
    public void A_pause_with_an_empty_reason_or_lever_is_refused(string reason, string lever, string message)
    {
        Action pause = () => FlowRunDecider.Pause(Build, Started(), reason, lever, "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(pause);
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void An_end_with_an_outcome_other_than_the_flows_is_refused()
    {
        Action end = () => FlowRunDecider.End(Build, Started(), "merged", "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(end);
        Assert.Equal("Flow 'build' ends with its declared outcome 'pr-opened', not 'merged'.", exception.Message);
    }

    [Theory]
    [InlineData(FlowRunStatus.Running)]
    [InlineData(FlowRunStatus.Ended)]
    [InlineData(FlowRunStatus.Stopped)]
    public void Resuming_a_flow_run_that_is_not_paused_is_refused(FlowRunStatus status)
    {
        Action resume = () => FlowRunDecider.Resume(Build, InStatus(status), "go on", "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(resume);
        Assert.EndsWith("; only a paused flow run resumes.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_paused_flow_run_resumes_and_runs_again()
    {
        FlowRun flowRun = InStatus(FlowRunStatus.Paused);
        Assert.True(flowRun.IsLive, "a paused flow run is still live");

        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, "answered", "owner", Now));

        Assert.Equal(FlowRunStatus.Running, flowRun.Status);
        Assert.Null(flowRun.Lever);
    }

    [Theory]
    [InlineData(FlowRunStatus.Ended, "has ended")]
    [InlineData(FlowRunStatus.Stopped, "has been stopped")]
    public void Stopping_a_flow_run_that_has_ended_or_stopped_is_refused(FlowRunStatus status, string described)
    {
        Action stop = () => FlowRunDecider.Stop(InStatus(status), "enough", "owner", Now);

        DomainException exception = Assert.Throws<DomainException>(stop);
        Assert.EndsWith($"{described}; only a live flow run stops.", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FlowRunStatus.Running)]
    [InlineData(FlowRunStatus.Paused)]
    public void A_live_flow_run_stops_and_releases_its_parcel(FlowRunStatus status)
    {
        FlowRun flowRun = InStatus(status);

        (FlowRunStopped stopped, ParcelFlowRunReleased released) = FlowRunDecider.Stop(flowRun, "enough", "owner", Now);
        flowRun.Apply(stopped);

        Assert.False(flowRun.IsLive);
        Assert.Equal(flowRun.ParcelId, released.ParcelId);
        Assert.Equal(flowRun.Id, released.FlowRunId);
    }

    [Fact]
    public void Recording_against_a_flow_other_than_the_one_the_flow_run_started_under_is_refused()
    {
        Action step = () => FlowRunDecider.StartStep(SoftwareFlows.Discovery, Started(), "discover", Now);

        DomainException exception = Assert.Throws<DomainException>(step);
        Assert.EndsWith("a flow run keeps the flow it started under.", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A flow run of <paramref name="flow"/> at its agent step 'build', launched, paused without an answer and resumed.</summary>
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
    public void A_respawn_of_a_resumed_unanswered_agent_step_counts_one_spawn_and_no_visit_once_per_resume()
    {
        FlowRun flowRun = ResumedUnanswered(Build);
        int spawns = flowRun.Spawns;
        int visits = flowRun.Position.VisitsTo("build");

        flowRun.Apply(FlowRunDecider.Respawn(Build, flowRun, "build", Now.AddMinutes(2)));

        Assert.Equal(spawns + 1, flowRun.Spawns);
        Assert.Equal(spawns + 1, flowRun.Position.Spawns);
        // the visit is performed again, not entered again
        Assert.Equal(visits, flowRun.Position.VisitsTo("build"));
        Assert.Equal(2, flowRun.Steps.Count);
        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Respawn(Build, flowRun, "build", Now.AddMinutes(3)));
        Assert.Contains("already counted a spawn for its latest resume", exception.Message, StringComparison.Ordinal);

        flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, "build", "agenthash", Model, Now.AddMinutes(3)));
        flowRun.Apply(FlowRunDecider.Pause(Build, flowRun, "no valid answer", "perform it again", "flow-run", Now.AddMinutes(4), Spend));
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, "once more", "owner", Now.AddMinutes(5)));
        flowRun.Apply(FlowRunDecider.Respawn(Build, flowRun, "build", Now.AddMinutes(6)));
        // each resume counts its own spawn
        Assert.Equal(spawns + 2, flowRun.Spawns);
    }

    [Fact]
    public void A_resumed_agent_step_that_paused_before_it_launched_uses_its_counted_spawn_rather_than_counting_another()
    {
        Flow capped = Build with { SpawnCap = 1 };
        FlowRun flowRun = ResumedUnanswered(capped, launched: false);

        Assert.NotNull(flowRun.Current);
        Assert.False(flowRun.Current?.SpawnsAgainOnResume, "the spawn counted when the step was entered was never used");
        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Respawn(capped, flowRun, "build", Now.AddMinutes(2)));
        Assert.Contains("has not launched its agent on the spawn it last counted", exception.Message, StringComparison.Ordinal);

        flowRun.Apply(FlowRunDecider.Spawn(capped, flowRun, "build", "agenthash", Model, Now.AddMinutes(2)));
        // a launch counts nothing itself
        Assert.Equal(1, flowRun.Spawns);
        Assert.False(flowRun.Current?.SpawnsAgainOnResume, "no resume has followed the launch");
    }

    [Fact]
    public void A_respawn_is_refused_at_the_spawn_cap_before_a_resume_and_for_a_system_step()
    {
        Flow capped = Build with { SpawnCap = 1 };
        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Respawn(capped, ResumedUnanswered(capped), "build", Now));
        Assert.Equal("Flow 'build' reached its spawn cap of 1; performing agent step 'build' again would spawn another agent.", exception.Message);

        DomainException exception2 = Assert.Throws<DomainException>(() => FlowRunDecider.Respawn(Build, AtAgentStep(), "build", Now));
        Assert.Contains("has not been resumed since it was entered", exception2.Message, StringComparison.Ordinal);

        FlowRun atSystemStep = Started();
        atSystemStep.Apply(FlowRunDecider.Pause(Build, atSystemStep, "waiting", "lever", "owner", Now));
        atSystemStep.Apply(FlowRunDecider.Resume(Build, atSystemStep, "go on", "owner", Now));
        DomainException exception3 = Assert.Throws<DomainException>(() => FlowRunDecider.Respawn(Build, atSystemStep, "worktree", Now));
        Assert.Contains("only an agent step spawns an agent", exception3.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_carries_the_agents_hash_and_model_spec_and_its_session_is_recorded_once_until_the_next_launch()
    {
        FlowRun flowRun = AtAgentStep();
        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.OpenSession(Build, flowRun, "build", Ids.New(), Now));
        Assert.Contains("has not recorded the launch of its agent", exception.Message, StringComparison.Ordinal);

        flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, "build", " agenthash ", Model, Now));
        Assert.Equal("agenthash", flowRun.Current?.AgentHash);
        Assert.Equal(Model, flowRun.Current?.Model);
        Assert.Null(flowRun.Current?.SessionId);
        Assert.Null(flowRun.Current?.Answer);

        Guid first = Ids.New();
        flowRun.Apply(FlowRunDecider.OpenSession(Build, flowRun, "build", first, Now.AddSeconds(1)));
        Assert.Equal(first, flowRun.Current?.SessionId);
        // recording a session counts nothing
        Assert.Equal(1, flowRun.Spawns);
        DomainException exception2 = Assert.Throws<DomainException>(() => FlowRunDecider.OpenSession(Build, flowRun, "build", Ids.New(), Now.AddSeconds(2)));
        Assert.Contains("already holds session", exception2.Message, StringComparison.Ordinal);

        flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, "build", "agenthash", Model, Now.AddSeconds(3)));
        // a launch has no session until the executor returns one
        Assert.Null(flowRun.Current?.SessionId);
        Assert.Equal(1, flowRun.Spawns);
    }

    [Fact]
    public void A_resumes_counted_spawn_clears_the_session_of_the_launch_before_it()
    {
        FlowRun flowRun = ResumedUnanswered(Build);
        flowRun.Apply(new AgentStepSessionOpened(flowRun.Id, "build", Ids.New(), Now.AddMinutes(1)));

        flowRun.Apply(FlowRunDecider.Respawn(Build, flowRun, "build", Now.AddMinutes(2)));

        Assert.Null(flowRun.Current?.SessionId);
        // the visit still names the agent it last launched
        Assert.Equal("agenthash", flowRun.Current?.AgentHash);
    }

    [Fact]
    public void A_launch_with_no_agent_hash_or_model_spec_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Spawn(Build, AtAgentStep(), "build", " ", Model, Now));
        Assert.Contains("content hash of the agent it launches", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(() => FlowRunDecider.Spawn(Build, AtAgentStep(), "build", "agenthash", Model with { Effort = "" }, Now));
        Assert.Contains("model spec its agent runs on", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_recorded_before_it_carried_the_agent_replays_with_neither_hash_nor_model_spec()
    {
        FlowRun flowRun = AtAgentStep();
        AgentStepSpawned old = JsonSerializer.Deserialize<AgentStepSpawned>(
            $$"""{"FlowRunId":"{{flowRun.Id}}","Step":"build","Agent":"build","SpawnedAt":"2026-11-03T12:00:00+00:00"}""")
            ?? throw new InvalidOperationException("The launch did not read back.");

        flowRun.Apply(old);

        Assert.Equal(Now, flowRun.Current?.SpawnedAt);
        Assert.Null(flowRun.Current?.AgentHash);
        Assert.Null(flowRun.Current?.Model);
    }

    [Fact]
    public void An_answer_from_the_session_a_resume_moved_past_is_refused_even_on_the_same_step_and_visit()
    {
        FlowRun flowRun = AtAgentStep();
        Guid held = Launched(flowRun);
        flowRun.Apply(FlowRunDecider.Pause(Build, flowRun, "Hold on.", "resume", "owner", Now));
        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, "owner", Now));

        // a resume does not clear the session it found
        Assert.Equal(held, flowRun.Current?.SessionId);
        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Answer(Build, flowRun, "build", "build", held, "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now));
        Assert.Matches(@"(?s)^Step 'build' was resumed after it launched session .*; an answer from session .* is not recorded on this visit\.$", exception.Message);
        DomainException exception2 = Assert.Throws<DomainException>(() => FlowRunDecider.Pause(Build, flowRun, "No valid answer.", "/transcripts/1.jsonl", "owner", Now, Spend, held));
        Assert.Matches(@"(?s)^Step 'build' was resumed after it launched session .*; a pause from session .* is not recorded on this visit\.$", exception2.Message);
    }

    [Fact]
    public void An_answer_from_a_session_the_latest_launch_did_not_return_is_refused()
    {
        FlowRun flowRun = AtAgentStep();
        Launched(flowRun);

        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Answer(Build, flowRun, "build", "build", Ids.New(), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now));
        Assert.Matches(@"(?s)^Step 'build' expects session .*; an answer from session .* is not recorded on this visit\.$", exception.Message);

        FlowRun unopened = AtAgentStep();
        unopened.Apply(FlowRunDecider.Spawn(Build, unopened, "build", "agenthash", Model, Now));
        DomainException exception2 = Assert.Throws<DomainException>(() => FlowRunDecider.Answer(Build, unopened, "build", "build", Ids.New(), "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now));
        Assert.Matches(@"(?s)^Step 'build' holds no session for its latest launch; an answer from session .* is not recorded on this visit\.$", exception2.Message);
    }

    [Theory]
    [InlineData(FlowRunStatus.Running)]
    [InlineData(FlowRunStatus.Paused)]
    [InlineData(FlowRunStatus.Stopped)]
    public void A_dropped_agent_steps_usage_counts_once_in_the_cost_and_leaves_the_rest_of_the_flow_run_as_it_was(FlowRunStatus status)
    {
        FlowRun flowRun = AtAgentStep();
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

        AgentStepUsageRecorded recorded = FlowRunDecider.RecordUsage(Build, flowRun, "build", "build", session, usage, Now.AddMinutes(1));
        flowRun.Apply(recorded);

        Assert.Equal(usage, recorded.Usage);
        Assert.Equal(before + usage.CostUsd, flowRun.Cost);
        Assert.Equal(status, flowRun.Status);
        Assert.Equal(reason, flowRun.PauseReason);
        Assert.Equal(lever, flowRun.Lever);
        Assert.Equal(pauseUsage, flowRun.PauseUsage);
        Assert.Equal(visit, flowRun.Current);
    }

    [Fact]
    public void A_dropped_agent_steps_usage_is_refused_on_an_ended_flow_run_another_step_or_a_system_step()
    {
        FlowRun ended = InStatus(FlowRunStatus.Ended);
        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.RecordUsage(Build, ended, "worktree", "build", Ids.New(), Spend, Now));
        Assert.EndsWith("has ended; an agent step's usage is recorded on a running, paused, or stopped flow run.", exception.Message, StringComparison.Ordinal);

        DomainException exception2 = Assert.Throws<DomainException>(() => FlowRunDecider.RecordUsage(Build, AtAgentStep(), "worktree", "build", Ids.New(), Spend, Now));
        Assert.EndsWith("is at step 'build'; an agent step's usage is recorded only for the current step, not 'worktree'.", exception2.Message, StringComparison.Ordinal);
        DomainException exception3 = Assert.Throws<DomainException>(() => FlowRunDecider.RecordUsage(Build, Started(), "worktree", "worktree", Ids.New(), Spend, Now));
        Assert.Equal("Step 'worktree' is a system step; only an agent step reports usage.", exception3.Message);
        DomainException exception4 = Assert.Throws<DomainException>(() => FlowRunDecider.RecordUsage(Build, AtAgentStep(), "build", "build", Ids.New(), Spend with { Tokens = -1 }, Now));
        Assert.StartsWith("A measurement is never negative", exception4.Message, StringComparison.Ordinal);
    }
}
