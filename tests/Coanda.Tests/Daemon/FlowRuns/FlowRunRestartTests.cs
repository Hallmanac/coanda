using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A daemon restart that finds an agent step launched, unanswered, and not resumed since: its session
/// is reattached and answers, or the flow run pauses when the session was lost; it is never launched again.
/// </summary>
public sealed class FlowRunRestartTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly string[] CleanPath =
    [
        "worktree", "build", "gates", "adversarial-review", "conformance-review", "open-pr", "move-to-review",
    ];

    [Fact]
    public async Task A_restart_reattaches_the_held_session_retries_it_once_and_records_its_answer_with_the_launchs_agent_and_no_second_spawn()
    {
        using var temp = new TemporaryHome();
        var script = Scripts.CleanBuild();
        script["build"] = [new ScriptEntry(null, "/transcripts/0.jsonl", Scripts.Usage(4)), Scripts.Answer("done", 1)];
        var stalled = new GatedExecutor(new ScriptedExecutor(script));
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Reattached");

        bool answeredBeforeStop = false;
        var held = new GatedExecutor(stalled.Inner)
        {
            BeforeStop = async handle =>
            {
                if (handle == issued)
                {
                    await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
                    FlowRun? now = await new FlowRunService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowRunId, None);
                    answeredBeforeStop = now?.Steps.Single(s => s.Step == "build").IsRecorded ?? false;
                }
            },
        };
        held.Open();

        // Published while no daemon runs, so the second start's reattach meets the new version.
        AgentDefinition republished = SoftwareAgents.Build with { Model = new ModelSpec("claude-code", "claude-republished", "low") };
        await PublishAgentAsync(temp, republished);
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = held }, None);
        using var again = new DaemonClient(temp.Home);

        FlowRunView ended = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");

        Assert.Equal(CleanPath, ended.Steps.Select(s => s.Step));
        FlowRunStepView build = ended.Steps.Single(s => s.Step == "build");
        // the answer is recorded with the agent the launch recorded
        Assert.Equal(SoftwareAgents.Build.Hash, build.AgentHash);
        Assert.NotEqual(republished.Hash, build.AgentHash);
        Assert.Equal(SoftwareAgents.Build.Model, build.Model);
        // both turns of the reattached session are its usage
        Assert.Equal(AgentStep.Sum(Scripts.Usage(4), Scripts.Usage(1)), build.Measurement);
        // the reattach counts no spawn: build and the two reviews
        Assert.Equal(3, ended.Spawns);
        // the executor records no second spawn
        Assert.Single(held.Inner.Spawns, s => s.Agent == "build");
        // an invalid answer is retried once on the same session
        Assert.Single(held.Inner.Resumes, r => r.Handle == issued);
        Assert.Single(held.Inner.Stops, handle => handle == issued);
        Assert.True(answeredBeforeStop, "the session is stopped after its answer is recorded");

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            IReadOnlyList<object> events = await EventsOfAsync(store, flowRunId);
            Assert.Single(events.OfType<AgentStepSpawned>(), e => e.Step == "build");
            Assert.Empty(events.OfType<AgentStepRespawned>());
        }

        Definition? resolved = await second.Services.GetRequiredService<DefinitionService>().ResolveAgentAsync(Principal.LocalOwner, Project, "build", None);
        // the republished agent stood while the session was reattached
        Assert.Equal(republished.Hash, resolved?.ContentHash);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattached_session_is_handed_a_token_the_new_daemon_issued_for_the_recorded_launch_which_resolves_until_its_answer_is_recorded()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Reissued");
        SessionAccess spawned = stalled.Inner.Spawns.Single().Access ?? throw new InvalidOperationException("The spawn carried no session access.");
        Guid launch = await LaunchIdAsync(temp, flowRunId);

        var held = new GatedExecutor(stalled.Inner);
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = held }, None);
        using var again = new DaemonClient(temp.Home);
        await held.Holding.WaitAsync(TimeSpan.FromSeconds(30));

        SessionAccess reissued = Assert.Single(held.Inner.Reattaches.Where(r => r.Handle == issued).Select(r => r.Access).OfType<SessionAccess>());
        Assert.Equal(temp.Home.Path, reissued.NodeHome);
        Assert.Equal(Project, reissued.Project);
        Assert.NotEqual(spawned.Token, reissued.Token);
        SessionTokens tokens = second.Services.GetRequiredService<SessionTokens>();
        Principal? principal = await tokens.ResolveAsync(reissued.Token, None);
        // the token is bound to the launch the restart found
        Assert.Equal(new SessionBinding(Project, flowRunId, launch), principal?.Session);
        // the token the earlier daemon issued is kept across the restart
        Assert.Equal(new SessionBinding(Project, flowRunId, launch), (await tokens.ResolveAsync(spawned.Token, None))?.Session);

        held.Open();
        await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");
        // the token is revoked once the reattached agent step ends
        Assert.Null(await tokens.ResolveAsync(reissued.Token, None));
        // so is the one the launch was spawned with
        Assert.Null(await tokens.ResolveAsync(spawned.Token, None));

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattach_that_fails_stops_the_session_before_the_flow_run_pauses_as_lost()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Unreachable");

        var failing = new GatedExecutor(stalled.Inner)
        {
            BeforeReattach = handle => throw new IOException($"The record of session {handle.Id} could not be read."),
        };
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = failing }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "paused");

        Assert.Equal($"Step 'build' lost the session agent 'build' was launched in across a daemon restart (session {Ids.Short(issued.Id)}, which could not be reattached), so its usage was not observed.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Equal([issued], failing.Inner.Stops);
        Assert.Single(failing.Inner.Spawns);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_during_the_retry_collects_the_retry_without_another_and_pauses_with_the_whole_sessions_usage()
    {
        using var temp = new TemporaryHome();
        var script = Scripts.CleanBuild();
        script["build"] = [new ScriptEntry(null, "/transcripts/0.jsonl", Scripts.Usage(4)), new ScriptEntry(null, "/transcripts/1.jsonl", Scripts.Usage(1))];

        // The first collect passes, so the daemon stops while the retry's collect is held.
        var stalled = new GatedExecutor(new ScriptedExecutor(script)) { FreeCollects = 1 };
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Stopped in the retry");
        Assert.Single(stalled.Inner.Resumes, r => r.Handle == issued);

        var answering = new GatedExecutor(stalled.Inner);
        answering.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = answering }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "paused");

        Assert.StartsWith("Agent 'build' gave no valid 'build' answer after one retry:", paused.PauseReason, StringComparison.Ordinal);
        // the first turn, which nothing recorded at the stop, is counted
        Assert.Equal(AgentStep.Sum(Scripts.Usage(4), Scripts.Usage(1)), paused.PauseUsage);
        // the retry before the restart was the one retry
        Assert.Single(answering.Inner.Resumes);
        Assert.Single(answering.Inner.Spawns);
        Assert.Equal([issued], answering.Inner.Stops);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_pause_saved_after_the_start_up_sweep_and_before_the_flow_runs_first_read_stops_the_session_it_left_running()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Paused at the start");

        // The sweep has read the flow run as running when it lists the sessions, so it leaves this one
        // for the flow run's advance; the pause lands before that advance reads the flow run.
        var answering = new GatedExecutor(stalled.Inner)
        {
            BeforeList = async () =>
            {
                await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
                await new FlowRunService(store, TimeProvider.System).PauseAsync(
                    Principal.LocalOwner, Project, flowRunId, "A person paused it.", FlowRunLevers.PerformAgain("build"), None);
            },
        };
        answering.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = answering }, None);
        await FlowRunDriver.UntilAsync(() => answering.Inner.Stops.Contains(issued), "the session the sweep left is stopped");

        Assert.Equal([issued], answering.Inner.Stops);
        Assert.False(answering.Collecting.IsCompleted, "a paused flow run's session is not collected");
        Assert.Single(answering.Inner.Spawns);
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.ShowAsync(again, Project, flowRunId);
        Assert.Equal("paused", paused.Status);
        Assert.Equal("A person paused it.", paused.PauseReason);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_pause_saved_before_the_reattach_is_watched_stops_the_reattached_session_before_it_is_collected()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Paused in the window");

        // The pause lands after the advance read the flow run and before the reattach is watched, so
        // no agent step is there for it to end; only the reattach reading the flow run again sees it.
        var answering = new GatedExecutor(stalled.Inner)
        {
            BeforeReattach = async _ =>
            {
                await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
                await new FlowRunService(store, TimeProvider.System).PauseAsync(
                    Principal.LocalOwner, Project, flowRunId, "A person paused it.", FlowRunLevers.PerformAgain("build"), None);
            },
        };
        answering.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = answering }, None);
        await FlowRunDriver.UntilAsync(() => answering.Inner.Stops.Contains(issued), "the reattached session is stopped");

        Assert.False(answering.Collecting.IsCompleted, "a paused flow run's session is not collected");
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.ShowAsync(again, Project, flowRunId);
        Assert.Equal("paused", paused.Status);
        Assert.Equal("A person paused it.", paused.PauseReason);
        Assert.Null(paused.Steps.Single(s => s.Step == "build").RecordedAt);
        Assert.Equal([issued], answering.Inner.Stops);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattached_agent_step_whose_wall_clock_limit_ran_out_since_its_launch_is_stopped_and_pauses_without_a_second_spawn()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Out of time");
        TimeSpan limit = TimeSpan.FromSeconds(1);
        DateTimeOffset launched = await LaunchedAtAsync(temp, flowRunId);
        await FlowRunDriver.UntilAsync(() => DateTimeOffset.UtcNow - launched > limit + TimeSpan.FromMilliseconds(100), "the limit has passed since the launch");

        // The gate is open, so a limit measured from the reattach would let the session answer.
        stalled.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled, AgentStepLimit = limit }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "paused");

        Assert.Equal("Step 'build' ran past the wall-clock limit of 1 seconds for one agent step, so its session was stopped.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Equal(1, paused.Spawns);
        Assert.Single(stalled.Inner.Spawns);
        Assert.Equal([issued], stalled.Inner.Stops);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattached_session_that_finished_while_no_daemon_ran_records_its_answer_though_its_limit_has_passed_since()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Finished late");
        TimeSpan limit = TimeSpan.FromSeconds(1);
        DateTimeOffset launched = await LaunchedAtAsync(temp, flowRunId);
        await FlowRunDriver.UntilAsync(() => DateTimeOffset.UtcNow - launched > limit + TimeSpan.FromMilliseconds(100), "the limit has passed since the launch");

        // The session ended with its answer while no daemon ran; collecting it takes no more time.
        stalled.Inner.Finish(issued);
        stalled.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled, AgentStepLimit = limit }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView ended = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");

        Assert.Equal(CleanPath, ended.Steps.Select(s => s.Step));
        // the answer the session left is recorded
        Assert.NotNull(ended.Steps.Single(s => s.Step == "build").RecordedAt);
        // the reattach counts no spawn: build and the two reviews
        Assert.Equal(3, ended.Spawns);
        Assert.Single(stalled.Inner.Spawns, s => s.Agent == "build");
        // the session is stopped once its answer is recorded
        Assert.Contains(issued, stalled.Inner.Stops);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_whose_executor_no_longer_holds_the_session_pauses_and_its_resume_counts_one_more_spawn()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Lost");

        ScriptedExecutor fresh = Scripts.CleanBuildExecutor();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = fresh }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "paused");

        Assert.Equal($"Step 'build' lost the session agent 'build' was launched in across a daemon restart (session {Ids.Short(issued.Id)}, which the executor no longer holds), so its usage was not observed.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Equal(1, paused.Spawns);
        Assert.Null(paused.PauseUsage);
        // a lost session is not launched again
        Assert.Empty(fresh.Spawns);

        await FlowRunDriver.ResumeAsync(again, Project, flowRunId, "Perform it again.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");
        Assert.Equal(CleanPath, ended.Steps.Select(s => s.Step));
        // the resume counts one more spawn for build
        Assert.Equal(4, ended.Spawns);
        Assert.Equal(["build", "reviewer-adversarial", "reviewer-conformance"], fresh.Spawns.Select(s => s.Agent));

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_that_finds_the_session_lost_on_its_retry_pauses_with_the_usage_of_its_turn_before_the_retry()
    {
        using var temp = new TemporaryHome();
        var script = Scripts.CleanBuild();
        script["build"] = [new ScriptEntry(null, "/transcripts/0.jsonl", Scripts.Usage(4)), Scripts.Answer("done", 1)];

        // The first collect passes, so the daemon stops while the retry's collect is held; the retry then ends unanswered.
        var stalled = new GatedExecutor(new ScriptedExecutor(script)) { FreeCollects = 1 };
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Lost in the retry");
        Assert.Single(stalled.Inner.Resumes, r => r.Handle == issued);
        stalled.Inner.Lose(issued);

        var restarted = new GatedExecutor(stalled.Inner);
        restarted.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = restarted }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "paused");

        Assert.Equal($"Step 'build' lost the session agent 'build' was launched in across a daemon restart (session {Ids.Short(issued.Id)}, which ended on its retry without an answer), so the usage of its retry was not observed; the usage of its turns before the retry is recorded.", paused.PauseReason);
        // the first turn, which nothing recorded at the stop, is counted
        Assert.Equal(Scripts.Usage(4), paused.PauseUsage);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Equal(1, paused.Spawns);
        // a lost session is not launched again
        Assert.Single(restarted.Inner.Spawns);

        await FlowRunDriver.ResumeAsync(again, Project, flowRunId, "Perform it again.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");
        Assert.Equal(CleanPath, ended.Steps.Select(s => s.Step));
        // the resume counts one more spawn for build
        Assert.Equal(4, ended.Spawns);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_that_finds_a_launch_with_no_session_recorded_pauses_and_its_resume_is_held_to_the_spawn_cap()
    {
        using var temp = new TemporaryHome();
        Guid flowRunId;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            var parcels = new Coanda.Daemon.Parcels.ParcelService(store, TimeProvider.System);
            var flowRuns = new FlowRunService(store, TimeProvider.System);
            var parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Never recorded", null, None);
            await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            FlowRun flowRun = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            await flowRuns.RecordResultAsync(
                Principal.LocalOwner, Project, flowRun.Id, "worktree", SoftwareSteps.Worktree, JsonSerializer.SerializeToElement(new { verdict = "done" }), None);
            await flowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRun.Id, "build", None);
            await flowRuns.SpawnedAsync(Principal.LocalOwner, Project, flowRun.Id, "build", SoftwareAgents.Build.Hash, SoftwareAgents.Build.Model, None);
            flowRunId = flowRun.Id;
        }

        ScriptedExecutor executor = Scripts.CleanBuildExecutor();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        using var client = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        Assert.Equal("Step 'build' lost the session agent 'build' was launched in across a daemon restart (a session the daemon stopped before it was recorded), so its usage was not observed.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Empty(executor.Spawns);

        // The resume counts its spawn like any other resume, so the cap is reached by the same count.
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, null);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");
        Assert.Equal(4, ended.Spawns);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_start_stops_every_held_session_no_running_flow_run_waits_on_and_reattaches_the_one_a_flow_run_does()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Swept");
        SessionHandle orphan = await stalled.Inner.SpawnAsync(
            SoftwareAgents.Build, "Nobody waits on this.", temp.Home.FlowRunWorkPath(Project, Ids.New()), new SpawnCaps(1m, TimeSpan.FromMinutes(1)), AnswerSchemas.Build, null, null, None);

        stalled.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView ended = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");

        Assert.Equal(CleanPath, ended.Steps.Select(s => s.Step));
        // the unclaimed session is stopped before any flow run advances
        Assert.Equal(orphan, stalled.Inner.Stops[0]);
        Assert.Single(stalled.Inner.Stops, handle => handle == orphan);
        Assert.Single(stalled.Inner.Stops, handle => handle == issued);
        Assert.Equal([issued, orphan], stalled.Inner.Spawns.Where(s => s.Agent == "build").Select(s => s.Handle));
        Assert.Equal(Scripts.Usage(1), ended.Steps.Single(s => s.Step == "build").Measurement);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattached_collect_that_throws_a_task_cancellation_of_its_own_pauses_and_stops_the_session_once()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Gave up after the restart");

        var givingUp = new GatedExecutor(stalled.Inner) { CollectsGiveUp = true };
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = givingUp }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView paused = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "paused");

        Assert.StartsWith("Step 'build' got no answer from agent 'build':", paused.PauseReason, StringComparison.Ordinal);
        Assert.Contains("gave up collecting", paused.PauseReason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Equal([issued], givingUp.Inner.Stops);
        Assert.Equal([false], givingUp.StopTokensCancelled);
        Assert.Single(givingUp.Inner.Spawns);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_daemon_stop_during_a_reattached_collect_leaves_the_session_held_and_the_next_start_reattaches_it_again()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowRunId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Restarted twice");

        var heldAgain = new GatedExecutor(stalled.Inner);
        await using (RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = heldAgain }, None))
        {
            await heldAgain.Holding.WaitAsync(TimeSpan.FromSeconds(15));
            await second.StopAsync(None);
        }

        // a daemon stop during a reattached collect leaves the session held
        Assert.Empty(heldAgain.Inner.Stops);
        Assert.Equal([issued], await heldAgain.ListAsync(None));
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            FlowRun? between = await new FlowRunService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowRunId, None);
            Assert.Equal(FlowRunStatus.Running, between?.Status);
            Assert.Equal(issued.Id, between?.Current?.SessionId);
        }

        var answering = new GatedExecutor(stalled.Inner);
        answering.Open();
        await using RunningDaemon third = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = answering }, None);
        using var client = new DaemonClient(temp.Home);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal(CleanPath, ended.Steps.Select(s => s.Step));
        Assert.Equal(3, ended.Spawns);
        Assert.Single(answering.Inner.Spawns, s => s.Agent == "build");
        Assert.Single(answering.Inner.Stops, handle => handle == issued);

        await third.StopAsync(None);
    }

    [Fact]
    public async Task A_resumed_visit_records_what_stopping_its_earlier_session_reports_before_it_launches_another()
    {
        using var temp = new TemporaryHome();
        Measurement left = Scripts.Usage(3);
        IDocumentStore? store = null;
        Guid flowRunId = Guid.Empty;
        var costAtRelaunch = new TaskCompletionSource<decimal>(TaskCreationOptions.RunContinuationsAsynchronously);
        GatedExecutor? stopping = null;
        stopping = new GatedExecutor(Scripts.CleanBuildExecutor())
        {
            FailStops = true,
            CollectsGiveUp = true,
            InProgressUsage = left,
            BeforeSpawn = async _ =>
            {
                if (stopping?.Inner.Spawns.Count == 1 && store is not null)
                {
                    FlowRun? relaunching = await new FlowRunService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowRunId, None);
                    costAtRelaunch.TrySetResult(relaunching?.Cost ?? -1m);
                }
            },
        };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stopping }, None);
        store = daemon.Services.GetRequiredService<IDocumentStore>();
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Left running, then stopped");
        flowRunId = (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");
        Assert.StartsWith("Step 'build' got no answer from agent 'build':", paused.PauseReason, StringComparison.Ordinal);
        SessionHandle issued = Assert.Single(stopping.Inner.Spawns).Handle;
        // its stop after the pause failed
        Assert.Equal(issued, Assert.Single(await stopping.ListAsync(None)));

        stopping.FailStops = false;
        stopping.CollectsGiveUp = false;
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Again.");
        decimal relaunchCost = await costAtRelaunch.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // the stop's report is recorded before the new launch
        Assert.Equal(paused.Cost + left.CostUsd, relaunchCost);
        Assert.Equal([issued], stopping.Inner.Stops);
        await using (IQuerySession session = store.QuerySession(Project))
        {
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(flowRunId, token: None);
            Assert.Equivalent(new { Step = "build", Agent = "build", SessionId = issued.Id, Usage = left }, Assert.Single(events.Select(e => e.Data).OfType<AgentStepUsageRecorded>()));
        }

        await stopping.Holding.WaitAsync(TimeSpan.FromSeconds(15));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resumed_visit_whose_stopped_earlier_session_reports_usage_reaching_the_budget_pauses_on_it_without_launching_another()
    {
        using var temp = new TemporaryHome();
        var stopping = new GatedExecutor(Scripts.CleanBuildExecutor())
        {
            FailStops = true,
            CollectsGiveUp = true,
            InProgressUsage = new Measurement(1000, SoftwareFlows.Build.Budget, 1, TimeSpan.FromSeconds(30)),
        };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stopping }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Spent by its stop");
        Guid flowRunId = (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;
        Assert.StartsWith("Step 'build' got no answer from agent 'build':", (await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused")).PauseReason, StringComparison.Ordinal);
        SessionHandle issued = Assert.Single(stopping.Inner.Spawns).Handle;

        stopping.FailStops = false;
        stopping.CollectsGiveUp = false;
        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Again.");
        await FlowRunDriver.UntilAsync(async () => (await FlowRunDriver.ShowAsync(client, Project, flowRunId)).Lever == FlowRunLevers.BudgetReached(), "the resume pauses on the budget");
        FlowRunView spent = await FlowRunDriver.ShowAsync(client, Project, flowRunId);

        Assert.Equal("paused", spent.Status);
        Assert.True(spent.Cost >= SoftwareFlows.Build.Budget, $"Expected at least {SoftwareFlows.Build.Budget}, but found {spent.Cost}.");
        Assert.StartsWith($"Flow run {Ids.Short(flowRunId)} has recorded a cost of", spent.PauseReason, StringComparison.Ordinal);
        Assert.Equal([issued], stopping.Inner.Stops);
        // the budget the stopped session's usage reached launches no other agent
        Assert.Single(stopping.Inner.Spawns);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resumed_visit_whose_earlier_session_will_not_stop_pauses_without_launching_another()
    {
        using var temp = new TemporaryHome();
        var stubborn = new GatedExecutor(new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [new ScriptEntry(null, "/transcripts/1.jsonl", Scripts.Usage(1)), new ScriptEntry(null, "/transcripts/2.jsonl", Scripts.Usage(2))],
        }))
        { FailStops = true };
        stubborn.Open();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stubborn }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Will not stop");
        Guid flowRunId = (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;
        Assert.StartsWith("Agent 'build' gave no valid 'build' answer", (await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused")).PauseReason, StringComparison.Ordinal);
        SessionHandle issued = Assert.Single(stubborn.Inner.Spawns).Handle;
        // its stop after the pause failed
        Assert.Equal(issued, Assert.Single(await stubborn.ListAsync(None)));

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Again.");
        await FlowRunDriver.UntilAsync(async () => (await FlowRunDriver.ShowAsync(client, Project, flowRunId)).PauseReason?.Contains("could not stop", StringComparison.Ordinal) == true, "the resume pauses on the stop");
        FlowRunView paused = await FlowRunDriver.ShowAsync(client, Project, flowRunId);

        Assert.StartsWith($"Step 'build' could not stop session {Ids.Short(issued.Id)} that agent 'build' was left running in", paused.PauseReason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        // no second agent is launched beside one that would not stop
        Assert.Single(stubborn.Inner.Spawns);

        await daemon.StopAsync(None);
    }

    /// <summary>
    /// Starts a daemon on the gated executor, waits until the bound flow run's agent step has recorded its
    /// session and is held in its collect, and stops the daemon, checking that the session was left
    /// alive and the flow run names it.
    /// </summary>
    internal static async Task<(Guid FlowRunId, SessionHandle Issued)> StopWithSessionLeftAsync(TemporaryHome temp, GatedExecutor stalled, string title)
    {
        Guid flowRunId;
        Guid sessionId = Guid.Empty;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            using var client = new DaemonClient(temp.Home);
            FlowRunService flowRuns = first.Services.GetRequiredService<FlowRunService>();
            ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, title);
            flowRunId = (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;
            await stalled.Holding.WaitAsync(TimeSpan.FromSeconds(15));
            await FlowRunDriver.UntilAsync(
                async () =>
                {
                    FlowRun? flowRun = await flowRuns.ShowAsync(Principal.LocalOwner, Project, flowRunId, None);
                    sessionId = flowRun?.Current?.SessionId ?? Guid.Empty;
                    return sessionId != Guid.Empty;
                },
                "the flow run names its agent step's session");
            await first.StopAsync(None);
        }

        SessionHandle issued = Assert.Single(stalled.Inner.Spawns).Handle;
        Assert.Equal(sessionId, issued.Id);
        // a daemon stop leaves a recorded session for a reattach
        Assert.Empty(stalled.Inner.Stops);
        Assert.Empty(stalled.StopTokensCancelled);
        Assert.Equal([issued], await stalled.ListAsync(None));

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            FlowRun? between = await new FlowRunService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowRunId, None);
            Assert.NotNull(between);
            // a daemon stop is not a pause
            Assert.Equal(FlowRunStatus.Running, between.Status);
            Assert.False(between.Current?.IsRecorded);
            Assert.Equal(issued.Id, between.Current?.SessionId);
            Assert.True(between.Current?.AwaitsLaunchedSession);
        }

        return (flowRunId, issued);
    }

    private static async Task<Guid> LaunchIdAsync(TemporaryHome temp, Guid flowRunId)
    {
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        FlowRun? flowRun = await new FlowRunService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowRunId, None);
        return flowRun?.Current?.LaunchId ?? throw new InvalidOperationException("The flow run's visit recorded no launch id.");
    }

    private static async Task<DateTimeOffset> LaunchedAtAsync(TemporaryHome temp, Guid flowRunId)
    {
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        FlowRun? flowRun = await new FlowRunService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowRunId, None);
        return flowRun?.Current?.SpawnedAt ?? throw new InvalidOperationException("The flow run's visit recorded no launch.");
    }

    private static async Task PublishAgentAsync(TemporaryHome temp, AgentDefinition agent)
    {
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Agent, DefinitionScope.Project, Project, agent.Name, AgentDefinition.CanonicalJson(agent), agent.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        Definition? resolved = await definitions.ResolveAgentAsync(Principal.LocalOwner, Project, agent.Name, None);
        // the republished agent is what resolution now returns
        Assert.Equal(agent.Hash, resolved?.ContentHash);
    }

    private static async Task<IReadOnlyList<object>> EventsOfAsync(IDocumentStore store, Guid flowRunId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(flowRunId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
