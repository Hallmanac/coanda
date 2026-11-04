using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Store;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

public sealed class FlowRunAdvancerTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly string[] CleanPath =
    [
        "worktree", "build", "gates", "adversarial-review", "conformance-review", "open-pr", "move-to-review",
    ];

    [Fact]
    public async Task A_task_moved_to_build_runs_the_build_flows_clean_path_to_its_end_with_no_flow_start()
    {
        using var temp = new TemporaryHome();
        ScriptedExecutor executor = Scripts.CleanBuildExecutor();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Run the clean path");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        flowRun.Outcome.Should().Be("pr-opened");
        flowRun.FlowName.Should().Be("build");
        FlowRunView merge = await FlowRunDriver.MergedAsync(client, Project, parcel.Id, started.Id);
        (await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id)).Select(f => f.Id)
            .Should().Equal([started.Id, merge.Id], "the binding starts one flow run for the one entry to build, and the merge follows its end");
        flowRun.Steps.Select(s => s.Step).Should().Equal(CleanPath, "each go-to records the step it enters");
        flowRun.Steps.Should().OnlyContain(s => s.RecordedAt != null);
        ExpectJudgment(flowRun.Steps[1], ShippedAgents.Build, 1);
        ExpectJudgment(flowRun.Steps[3], ShippedAgents.ReviewerAdversarial, 2);
        ExpectJudgment(flowRun.Steps[4], ShippedAgents.ReviewerConformance, 3);
        flowRun.Steps[0].Result.GetValueOrDefault().GetProperty("branch").GetString().Should().Be($"coanda/{Ids.Short(parcel.Id)}");
        JsonElement gates = flowRun.Steps[2].Result.GetValueOrDefault();
        gates.GetProperty("verdict").GetString().Should().Be("done");
        gates.TryGetProperty("placeholder", out _).Should().BeFalse("the gates step runs the project's build and test commands");

        ParcelView after = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None);
        after.Stage.Should().Be("closed", "the build moved the task to review, and the merge that followed closed it");
        after.Resolution.Should().Be("completed");
        after.LiveFlowRunId.Should().BeNull();

        ScriptedSpawn build = executor.Spawns[0];
        build.WorkingDirectory.Should().Be(temp.Home.FlowRunWorkPath(Project, started.Id));
        build.Caps.Should().Be(new SpawnCaps(ShippedFlows.Build.Budget, DaemonOptions.DefaultJudgmentLimit));
        build.CredentialProfile.Should().BeNull();
        build.AnswerSchema.Should().Be("build");
        build.Context.Should().Contain("Title: Run the clean path").And.Contain("Step: build");
        executor.Stops.Should().HaveCount(3, "the runner ends each judgment's session once its answer is in hand");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_parcel_move_the_move_parcel_step_records_carries_the_flow_runs_principal()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Who moved it");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Project);
        var events = await session.Events.FetchStreamAsync(parcel.Id, token: None);
        ParcelMoved toReview = events.Select(e => e.Data).OfType<ParcelMoved>().Should().ContainSingle(m => m.ToStage == "review").Subject;
        toReview.MovedBy.Should().Be(FlowRunAdvancer.PrincipalFor(started.Id).Id).And.Be($"flow-run:{started.Id}");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_review_that_stays_not_clean_past_its_cap_pauses_with_the_interpreters_reason()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1)],
            ["reviewer-adversarial"] = [.. Enumerable.Range(1, 4).Select(n => Scripts.Answer("not-clean", n))],
            ["fix"] = [.. Enumerable.Range(1, 3).Select(n => Scripts.Answer("done", n))],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Never clean");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        flowRun.CurrentStep.Should().Be("adversarial-review");
        flowRun.PauseReason.Should().Be("Step 'adversarial-review' reached the visit cap of 3 on its transition to 'fix'.");
        flowRun.Lever.Should().Be(FlowRunLevers.PerformAgain("adversarial-review"));
        flowRun.Steps.Count(s => s.Step == "adversarial-review").Should().Be(4);
        flowRun.Steps[^1].Answer.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("not-clean");

        ParcelView after = await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None);
        after.Stage.Should().Be("build", "a flow run pause leaves the parcel in its stage");
        after.LiveFlowRunId.Should().Be(started.Id);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_at_a_review_whose_transitions_have_all_spent_their_visit_caps_pauses_again_without_spawning()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1)],
            ["reviewer-adversarial"] = [.. Enumerable.Range(1, 6).Select(n => Scripts.Answer("not-clean", n))],
            ["fix"] = [.. Enumerable.Range(1, 3).Select(n => Scripts.Answer("done", n))],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Never clean");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        FlowRunView first = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        first.Steps.Count(s => s.Step == "adversarial-review").Should().Be(4);
        int spawnsAtFirst = executor.Spawns.Count;

        // The clean transition is capped at 5, so a fifth visit could still route, and the resume performs it.
        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "One more look.");
        FlowRunView second = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        executor.Spawns.Should().HaveCount(spawnsAtFirst + 1);
        second.Steps.Count(s => s.Step == "adversarial-review").Should().Be(5);
        second.PauseReason.Should().Be("Step 'adversarial-review' reached the visit cap of 3 on its transition to 'fix'.");
        second.Lever.Should().Be(FlowRunLevers.PerformAgain("adversarial-review"));
        List<ScriptedSpawn> spawnsAtSecond = [.. executor.Spawns];

        // No transition out of the step allows a sixth visit, so the resume pauses before entering it.
        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "And another.");
        FlowRunView third = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        third.Lever.Should().Be(FlowRunLevers.VisitCapsSpent("adversarial-review"));
        third.PauseReason.Should().Be("Step 'adversarial-review' has a visit count of 5, and every transition out of it has a visit cap below 6; performing it again could only pause on a visit cap.");
        executor.Spawns.Should().Equal(spawnsAtSecond);
        JsonSerializer.Serialize(third.Steps).Should().Be(JsonSerializer.Serialize(second.Steps), "the resume entered no step and recorded nothing");
        third.Spawns.Should().Be(second.Spawns);

        // Resuming that pause pauses the same way, still performing nothing.
        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Once more.");
        FlowRunView fourth = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        fourth.Lever.Should().Be(FlowRunLevers.VisitCapsSpent("adversarial-review"));
        fourth.PauseReason.Should().Be(third.PauseReason);
        executor.Spawns.Should().Equal(spawnsAtSecond);
        JsonSerializer.Serialize(fourth.Steps).Should().Be(JsonSerializer.Serialize(second.Steps), "the resume entered no step and recorded nothing");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_wait_for_human_step_pauses_with_its_lever_and_a_resume_through_the_api_carries_the_flow_run_to_its_end()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        var hold = new Flow
        {
            Name = "hold-for-a-person",
            Accepts = ["task"],
            Outcome = "approved",
            Budget = 1m,
            SpawnCap = 1,
            Steps =
            [
                new FlowStep
                {
                    Name = "approve",
                    Action = CoreActions.WaitForHuman,
                    Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Read the task and resume to approve it.") },
                    Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
                },
            ],
        };
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, hold.Name, Flow.CanonicalJson(hold), hold.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Needs a nod", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, hold.Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        paused.Lever.Should().Be("Read the task and resume to approve it.");
        paused.PauseReason.Should().Contain("Step 'approve'");

        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Approved.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        ended.Outcome.Should().Be("approved");
        FlowRunStepView approve = ended.Steps.Should().ContainSingle().Subject;
        approve.Result.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("done");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_second_request_while_a_judgment_runs_does_not_perform_the_step_twice()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(Scripts.CleanBuildExecutor());
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IFlowRunAdvancer advancer = daemon.Services.GetRequiredService<IFlowRunAdvancer>();
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Only once");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        await executor.Spawned.WaitAsync(TimeSpan.FromSeconds(15));
        Task joined = advancer.RequestAdvance(Project, started.Id);
        Task again = advancer.RequestAdvance(Project, started.Id);
        executor.Open();
        await Task.WhenAll(joined, again).WaitAsync(TimeSpan.FromSeconds(15));

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");
        executor.Inner.Spawns.Count(s => s.Agent == "build").Should().Be(1);
        FlowRunStepView build = flowRun.Steps.Should().ContainSingle(s => s.Step == "build").Subject;
        build.Answer.Should().NotBeNull();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_with_no_answer_pauses_its_flow_run_naming_the_agent_while_another_flow_run_reaches_its_end()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView idea = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "idea", "Think first", null), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, idea.Id, "discovery"), None);
        ParcelView task = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Build anyway");

        FlowRunView discovery = await FlowRunDriver.BoundAsync(client, Project, idea.Id);
        FlowRunView build = await FlowRunDriver.BoundAsync(client, Project, task.Id);

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, discovery.Id, "paused");
        paused.PauseReason.Should().StartWith("Step 'discover' got no judgment from agent 'discovery':").And.Contain("agent 'discovery'");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("discover"));
        paused.Steps.Should().ContainSingle().Which.Answer.Should().BeNull();
        (await FlowRunDriver.UntilAsync(client, Project, build.Id, "ended")).Outcome.Should().Be("pr-opened");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_judgment_past_the_wall_clock_limit_is_stopped_and_pauses_its_flow_run_with_no_answer()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(Scripts.CleanBuildExecutor());
        var options = new DaemonOptions { Executor = executor, JudgmentLimit = TimeSpan.FromMilliseconds(300) };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, options, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Too slow");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        flowRun.PauseReason.Should().Be("Step 'build' ran past the wall-clock limit of 0.3 seconds for one judgment, so its session was stopped.");
        flowRun.Lever.Should().Be(FlowRunLevers.PerformAgain("build"));
        flowRun.Steps[^1].Step.Should().Be("build");
        flowRun.Steps[^1].Answer.Should().BeNull();
        executor.Inner.Stops.Should().ContainSingle().Which.Agent.Should().Be("build");
        executor.StopTokensCancelled.Should().Equal(false);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_judgment_past_the_wall_clock_limit_whose_session_will_not_stop_still_pauses_naming_the_limit()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(Scripts.CleanBuildExecutor()) { FailStops = true };
        var options = new DaemonOptions { Executor = executor, JudgmentLimit = TimeSpan.FromMilliseconds(300) };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, options, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Too slow to stop");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        flowRun.PauseReason.Should().StartWith("Step 'build' ran past the wall-clock limit of 0.3 seconds for one judgment, and stopping its session failed:");
        flowRun.Lever.Should().Be(FlowRunLevers.PerformAgain("build"));
        flowRun.Steps[^1].Answer.Should().BeNull();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_judgment_step_naming_its_agent_in_another_case_records_the_answer_under_the_steps_name()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>> { ["build"] = [Scripts.Answer("done", 1)] });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow shouted = await PublishAsync(daemon, new Flow
        {
            Name = "shouted-agent",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 1m,
            SpawnCap = 1,
            Steps = [Judge("build", "BUILD", Flow.End)],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Loud", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, shouted.Name);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        ended.Outcome.Should().Be("built");
        FlowRunStepView build = ended.Steps.Should().ContainSingle().Subject;
        build.Performer.Should().Be("BUILD");
        build.AgentHash.Should().Be(ShippedAgents.Build.Hash);
        executor.Spawns.Should().ContainSingle();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_after_a_spawn_cap_pause_pauses_again_without_spawning_past_the_cap()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1), Scripts.Answer("done", 2)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow capped = await PublishAsync(daemon, new Flow
        {
            Name = "one-spawn",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 1m,
            SpawnCap = 1,
            Steps = [Judge("first", "build", "second"), Judge("second", "build", Flow.End)],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Capped", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, capped.Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        paused.PauseReason.Should().Contain("reached its spawn cap of 1");

        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Try again.");
        FlowRunView after = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        after.PauseReason.Should().Be("Flow 'one-spawn' reached its spawn cap of 1; performing judgment step 'first' again would spawn another agent.");
        after.Lever.Should().Be(FlowRunLevers.SpawnCapReached(capped.Name));
        executor.Spawns.Should().ContainSingle("a resume never spawns past the flow's cap");
        after.Steps.Should().ContainSingle();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_of_a_judgment_that_paused_without_an_answer_counts_its_spawn_and_pauses_at_the_cap()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("maybe", 1), Scripts.Answer("maybe", 2), Scripts.Answer("maybe", 3), Scripts.Answer("maybe", 4)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow refused = await Publishing.FlowAsync(daemon, Project, new Flow
        {
            Name = "never-answers",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 2,
            Steps = [Judge("build", "build", Flow.End)],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Unanswered", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, refused.Name);
        FlowRunView first = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        first.PauseReason.Should().Contain("gave no valid 'build' answer");
        first.Spawns.Should().Be(1);

        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Try again.");
        FlowRunView second = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        second.PauseReason.Should().Contain("gave no valid 'build' answer");
        executor.Spawns.Should().HaveCount(2);
        second.Spawns.Should().Be(2, "the resume spawned the judgment again and counted it");
        second.Steps.Should().ContainSingle("the resume performed the same visit again rather than entering the step anew");
        second.Steps[0].Answer.Should().BeNull();

        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Once more.");
        FlowRunView capped = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        capped.PauseReason.Should().Be("Flow 'never-answers' reached its spawn cap of 2; performing judgment step 'build' again would spawn another agent.");
        capped.Lever.Should().Be(FlowRunLevers.SpawnCapReached(refused.Name));
        executor.Spawns.Should().HaveCount(2, "a resume never spawns past the flow's cap");
        capped.Spawns.Should().Be(2);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_of_a_judgment_that_paused_without_an_answer_routes_its_answer_within_a_visit_cap_of_one()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("maybe", 1), Scripts.Answer("maybe", 2), Scripts.Answer("done", 3)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow once = await Publishing.FlowAsync(daemon, Project, new Flow
        {
            Name = "answers-late",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 2,
            Steps = [Judge("build", "build", Flow.End)],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Late answer", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, once.Name);
        await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Try again.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        ended.Outcome.Should().Be("built");
        ended.Steps.Should().ContainSingle("the resume added no visit, so the visit cap of 1 still lets the answer route");
        ended.Spawns.Should().Be(2);
        executor.Spawns.Should().HaveCount(2);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resumed_judgment_that_answers_after_counting_its_spawn_routes_against_that_count_and_pauses_at_the_cap()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("maybe", 1), Scripts.Answer("maybe", 2), Scripts.Answer("done", 3), Scripts.Answer("done", 4)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow chained = await Publishing.FlowAsync(daemon, Project, new Flow
        {
            Name = "two-judgments",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 2,
            Steps = [Judge("a", "build", "b"), Judge("b", "build", Flow.End)],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Chained", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, chained.Name);
        await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Try again.");
        FlowRunView capped = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        capped.PauseReason.Should().Contain("spawn cap of 2", "the answer is routed against the spawn the resume counted");
        capped.Steps.Select(s => s.Step).Should().Equal(["a"], "step 'b' would spawn a third agent");
        capped.Spawns.Should().Be(2);
        executor.Spawns.Should().HaveCount(2);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_of_a_judgment_that_paused_before_its_agent_launched_performs_it_without_counting_another_spawn()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["late-builder"] = [Scripts.Answer("done", 1)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow late = await Publishing.FlowAsync(daemon, Project, new Flow
        {
            Name = "agent-published-late",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 1,
            Steps = [Judge("build", "late-builder", Flow.End)],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "No agent yet", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, late.Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        paused.PauseReason.Should().Contain("names the agent 'late-builder'");
        paused.Spawns.Should().Be(1);

        AgentDefinition agent = ShippedAgents.Build with { Name = "late-builder" };
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Agent, DefinitionScope.Project, Project, agent.Name, AgentDefinition.CanonicalJson(agent), agent.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "The agent is published now.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        ended.Outcome.Should().Be("built");
        ended.Spawns.Should().Be(1, "the spawn counted when the step was entered was the one the resume used");
        executor.Spawns.Should().ContainSingle();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_judgment_that_pauses_after_two_invalid_answers_shows_its_agent_hash_and_model_spec_beside_the_pauses_usage()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("maybe", 1), Scripts.Answer("maybe", 2)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow unanswered = await Publishing.FlowAsync(daemon, Project, new Flow
        {
            Name = "never-answers",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 2,
            Steps = [Judge("build", "build", Flow.End)],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Measured anyway", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, unanswered.Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        paused.PauseReason.Should().Contain("gave no valid 'build' answer");
        paused.PauseUsage.Should().Be(Scripts.Usage(3), "the pause reports both attempts' usage");
        FlowRunStepView build = paused.Steps.Should().ContainSingle().Subject;
        build.AgentHash.Should().Be(ShippedAgents.Build.Hash);
        build.Model.Should().Be(ShippedAgents.Build.Model);
        build.Answer.Should().BeNull();
        build.Measurement.Should().BeNull();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_session_a_spawn_returned_is_recorded_on_the_flow_run_before_its_first_collect()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(Scripts.CleanBuildExecutor());
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Named session");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        await executor.Collecting.WaitAsync(TimeSpan.FromSeconds(15));
        FlowRun? held = await flowRuns.ShowAsync(Principal.LocalOwner, Project, started.Id, None);

        SessionHandle issued = executor.Inner.Spawns.Should().ContainSingle().Subject.Handle;
        held.Should().NotBeNull();
        held.Current.Should().NotBeNull();
        held.Current?.Step.Should().Be("build");
        held.Current?.SessionId.Should().Be(issued.Id);

        executor.Open();
        (await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended")).Outcome.Should().Be("pr-opened");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_launch_whose_spawn_fails_uses_its_counted_spawn_holds_no_session_and_its_resume_counts_another()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1)],
        }))
        { FailingSpawns = 1 };
        executor.Open();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow twice = await Publishing.FlowAsync(daemon, Project, new Flow
        {
            Name = "launch-fails-once",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 2,
            Steps = [Judge("build", "build", Flow.End)],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Launch fails", null), None);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, twice.Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");
        paused.PauseReason.Should().StartWith("Step 'build' got no judgment from agent 'build':").And.Contain("could not be launched");
        paused.Spawns.Should().Be(1);
        FlowRun? failed = await flowRuns.ShowAsync(Principal.LocalOwner, Project, started.Id, None);
        failed.Should().NotBeNull();
        failed.Current?.SpawnedAt.Should().NotBeNull("the launch was recorded before the spawn failed, so its counted spawn is used");
        failed.Current?.SessionId.Should().BeNull("the spawn returned no session");
        executor.Inner.Spawns.Should().BeEmpty();

        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Try the launch again.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        ended.Outcome.Should().Be("built");
        ended.Spawns.Should().Be(2, "the resume of a used spawn counts another");
        ended.Steps.Should().ContainSingle();
        executor.Inner.Spawns.Should().ContainSingle();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_daemon_stop_cancels_the_judgment_in_progress_recording_nothing_and_the_next_start_carries_the_flow_run_on()
    {
        using var temp = new TemporaryHome();
        Guid flowRunId;
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            using var client = new DaemonClient(temp.Home);
            ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Interrupted");
            flowRunId = (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;
            await stalled.Spawned.WaitAsync(TimeSpan.FromSeconds(15));
            await first.StopAsync(None);
        }

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            FlowRun? between = await new FlowRunService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowRunId, None);
            between.Should().NotBeNull();
            between.Status.Should().Be(FlowRunStatus.Running, "a daemon stop is not a pause");
            between.CurrentStep.Should().Be("build");
            between.Current?.IsRecorded.Should().BeFalse("nothing is recorded for a step the stop cancelled");
        }

        // The fresh executor holds no session, so the launch's session is lost: the flow run pauses
        // rather than launch the agent again on a spawn it already used.
        ScriptedExecutor fresh = Scripts.CleanBuildExecutor();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = fresh }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "paused");
        paused.PauseReason.Should().StartWith("Step 'build' lost the session agent 'build' was launched in across a daemon restart")
            .And.EndWith("so its usage was not observed.");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("build"));
        fresh.Spawns.Should().BeEmpty();

        await FlowRunDriver.ResumeAsync(again, Project, flowRunId, "Perform it again.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");
        ended.Steps.Select(s => s.Step).Should().Equal(CleanPath);
        ended.Spawns.Should().Be(4, "the resume counts one more spawn for build");

        await second.StopAsync(None);
    }

    [Fact]
    public async Task An_executor_whose_collect_throws_a_task_cancellation_of_its_own_after_the_session_is_recorded_pauses_the_flow_run_and_stops_the_session_once()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(Scripts.CleanBuildExecutor()) { CollectsGiveUp = true };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Gave up");

        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        paused.PauseReason.Should().StartWith("Step 'build' got no judgment from agent 'build':").And.Contain("gave up collecting");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("build"));
        paused.Steps[^1].Answer.Should().BeNull();
        SessionHandle issued = executor.Inner.Spawns.Should().ContainSingle().Subject.Handle;
        FlowRun? flowRun = await flowRuns.ShowAsync(Principal.LocalOwner, Project, started.Id, None);
        flowRun?.Current?.SessionId.Should().Be(issued.Id, "the session was recorded before the collect gave up");
        executor.Inner.Stops.Should().Equal(issued);
        executor.StopTokensCancelled.Should().Equal(false);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_step_recorded_before_a_stop_is_routed_on_the_next_start_without_being_performed_again()
    {
        using var temp = new TemporaryHome();
        Guid flowRunId;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            var parcels = new Coanda.Daemon.Parcels.ParcelService(store, TimeProvider.System);
            var flowRuns = new FlowRunService(store, TimeProvider.System);
            var parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Half done", null, None);
            await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            FlowRun flowRun = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            await flowRuns.RecordResultAsync(
                Principal.LocalOwner, Project, flowRun.Id, "worktree", SoftwareActions.Worktree, JsonSerializer.SerializeToElement(new { verdict = "done" }), None);
            await flowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRun.Id, "build", None);
            Guid session = Ids.New();
            await flowRuns.SpawnedAsync(Principal.LocalOwner, Project, flowRun.Id, "build", ShippedAgents.Build.Hash, ShippedAgents.Build.Model, None);
            await flowRuns.SessionOpenedAsync(Principal.LocalOwner, Project, flowRun.Id, "build", session, None);
            await flowRuns.RecordAnswerAsync(
                Principal.LocalOwner, Project, flowRun.Id, "build", "build", session, ShippedAgents.Build.Hash, ShippedAgents.Build.Model,
                JsonSerializer.SerializeToElement(new { verdict = "done" }), Scripts.Usage(1), None);
            flowRunId = flowRun.Id;
        }

        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["reviewer-adversarial"] = [Scripts.Answer("clean", 2)],
            ["reviewer-conformance"] = [Scripts.Answer("clean", 3)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        using var client = new DaemonClient(temp.Home);

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");
        ended.Steps.Select(s => s.Step).Should().Equal(CleanPath);
        executor.Spawns.Select(s => s.Agent).Should().Equal("reviewer-adversarial", "reviewer-conformance");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_flow_run_stopped_while_its_judgment_runs_records_nothing_more_and_other_flow_runs_still_advance()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1), Scripts.Answer("done", 2)],
            ["reviewer-adversarial"] = [Scripts.Answer("clean", 3)],
            ["reviewer-conformance"] = [Scripts.Answer("clean", 4)],
        }));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView first = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Called off");

        FlowRunView doomed = await FlowRunDriver.BoundAsync(client, Project, first.Id);

        // Collecting, not Spawned: the stop is meant to find the judgment held in its collect, past
        // the spawn and the session record that follows it.
        await executor.Collecting.WaitAsync(TimeSpan.FromSeconds(15));
        await FlowRunDriver.StopAsync(client, Project, doomed.Id, "Not needed any more.");
        executor.Open();
        await FlowRunDriver.UntilAsync(() => executor.Inner.Stops.Count == 1, "the stopped flow run's session is ended");

        ParcelView second = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Still wanted");
        FlowRunView other = await FlowRunDriver.BoundAsync(client, Project, second.Id);
        (await FlowRunDriver.UntilAsync(client, Project, other.Id, "ended")).Outcome.Should().Be("pr-opened");

        FlowRunView stopped = await FlowRunDriver.ShowAsync(client, Project, doomed.Id);
        stopped.Status.Should().Be("stopped");
        stopped.Steps.Select(s => s.Step).Should().Equal("worktree", "build");
        stopped.Steps[^1].Answer.Should().BeNull();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_refused_start_answers_with_the_rule()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView idea = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "idea", "Not a task", null), None);

        await FluentActions.Awaiting(() => FlowRunDriver.StartAsync(client, Project, idea.Id, "build"))
            .Should().ThrowAsync<DomainException>().WithMessage("Flow 'build' accepts parcels of type task, not 'idea'.");

        await daemon.StopAsync(None);
    }

    private static FlowStep Judge(string name, string agent, string target) => new()
    {
        Name = name,
        Agent = agent,
        AnswerSchema = "build",
        Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = target }],
    };

    private static async Task<Flow> PublishAsync(RunningDaemon daemon, Flow flow)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        return flow;
    }

    private static void ExpectJudgment(FlowRunStepView step, AgentDefinition agent, int ordinal)
    {
        step.Kind.Should().Be("judgment");
        step.Performer.Should().Be(agent.Name);
        step.AgentHash.Should().Be(agent.Hash);
        step.Model.Should().Be(agent.Model);
        step.Measurement.Should().Be(Scripts.Usage(ordinal));
    }
}
