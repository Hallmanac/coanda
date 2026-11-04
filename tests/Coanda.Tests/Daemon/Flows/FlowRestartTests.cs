using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Flows.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>
/// A daemon restart that finds a judgment launched, unanswered, and not resumed since: its session
/// is reattached and answers, or the flow pauses when the session was lost; it is never launched again.
/// </summary>
public sealed class FlowRestartTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly string[] CleanPath =
    [
        "worktree", "build", "gates", "adversarial-review", "conformance-review", "open-pr", "gated-merge", "move-to-review",
    ];

    [Fact]
    public async Task A_restart_reattaches_the_held_session_retries_it_once_and_records_its_answer_with_the_launchs_agent_and_no_second_spawn()
    {
        using var temp = new TemporaryHome();
        var script = Scripts.CleanBuild();
        script["build"] = [new ScriptEntry(null, "/transcripts/0.jsonl", Scripts.Usage(4)), Scripts.Answer("done", 1)];
        var stalled = new GatedExecutor(new ScriptedExecutor(script));
        (Guid flowId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Reattached");

        bool answeredBeforeStop = false;
        var held = new GatedExecutor(stalled.Inner)
        {
            BeforeStop = async handle =>
            {
                if (handle == issued)
                {
                    await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
                    Flow? now = await new FlowService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowId, None);
                    answeredBeforeStop = now?.Steps.Single(s => s.Step == "build").IsRecorded ?? false;
                }
            },
        };
        held.Open();
        AgentDefinition republished = ShippedAgents.Build with { Model = new ModelSpec("claude-code", "claude-republished", "low") };
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = held }, None);
        await PublishAgentAsync(second, republished);
        using var again = new DaemonClient(temp.Home);

        FlowView ended = await Flowing.UntilAsync(again, Project, flowId, "ended");

        ended.Steps.Select(s => s.Step).Should().Equal(CleanPath);
        FlowStepView build = ended.Steps.Single(s => s.Step == "build");
        build.AgentHash.Should().Be(ShippedAgents.Build.Hash, "the answer is recorded with the agent the launch recorded");
        build.AgentHash.Should().NotBe(republished.Hash);
        build.Model.Should().Be(ShippedAgents.Build.Model);
        build.Measurement.Should().Be(Judgment.Sum(Scripts.Usage(4), Scripts.Usage(1)), "both turns of the reattached session are its usage");
        ended.Spawns.Should().Be(3, "the reattach counts no spawn: build and the two reviews");
        held.Inner.Spawns.Where(s => s.Agent == "build").Should().ContainSingle("the executor records no second spawn");
        held.Inner.Resumes.Should().ContainSingle(r => r.Handle == issued, "an invalid answer is retried once on the same session");
        held.Inner.Stops.Should().ContainSingle(handle => handle == issued);
        answeredBeforeStop.Should().BeTrue("the session is stopped after its answer is recorded");

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            IReadOnlyList<object> events = await EventsOfAsync(store, flowId);
            events.OfType<JudgmentSpawned>().Where(e => e.Step == "build").Should().ContainSingle();
            events.OfType<JudgmentRespawned>().Should().BeEmpty();
        }

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattached_judgment_whose_wall_clock_limit_ran_out_since_its_launch_is_stopped_and_pauses_without_a_second_spawn()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Out of time");
        TimeSpan limit = TimeSpan.FromSeconds(1);
        DateTimeOffset launched = await LaunchedAtAsync(temp, flowId);
        await Flowing.UntilAsync(() => DateTimeOffset.UtcNow - launched > limit + TimeSpan.FromMilliseconds(100), "the limit has passed since the launch");

        // The gate is open, so a limit measured from the reattach would let the session answer.
        stalled.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled, JudgmentLimit = limit }, None);
        using var again = new DaemonClient(temp.Home);
        FlowView paused = await Flowing.UntilAsync(again, Project, flowId, "paused");

        paused.PauseReason.Should().Be("Step 'build' ran past the wall-clock limit of 1 seconds for one judgment, so its session was stopped.");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("build"));
        paused.Spawns.Should().Be(1);
        stalled.Inner.Spawns.Should().ContainSingle();
        stalled.Inner.Stops.Should().Equal(issued);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_whose_executor_no_longer_holds_the_session_pauses_and_its_resume_counts_one_more_spawn()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Lost");

        ScriptedExecutor fresh = Scripts.CleanBuildExecutor();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = fresh }, None);
        using var again = new DaemonClient(temp.Home);
        FlowView paused = await Flowing.UntilAsync(again, Project, flowId, "paused");

        paused.PauseReason.Should().Be(
            $"Step 'build' lost the session agent 'build' was launched in across a daemon restart (session {Ids.Short(issued.Id)}, which the executor no longer holds), so its usage was not observed.");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("build"));
        paused.Spawns.Should().Be(1);
        paused.PauseUsage.Should().BeNull();
        fresh.Spawns.Should().BeEmpty("a lost session is not launched again");

        await Flowing.ResumeAsync(again, Project, flowId, "Perform it again.");
        FlowView ended = await Flowing.UntilAsync(again, Project, flowId, "ended");
        ended.Steps.Select(s => s.Step).Should().Equal(CleanPath);
        ended.Spawns.Should().Be(4, "the resume counts one more spawn for build");
        fresh.Spawns.Select(s => s.Agent).Should().Equal("build", "reviewer-adversarial", "reviewer-conformance");

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_that_finds_a_launch_with_no_session_recorded_pauses_and_its_resume_is_held_to_the_spawn_cap()
    {
        using var temp = new TemporaryHome();
        Guid flowId;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            var parcels = new Coanda.Daemon.Parcels.ParcelService(store, TimeProvider.System);
            var flows = new FlowService(store, TimeProvider.System);
            var parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Never recorded", null, None);
            await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            Flow flow = await flows.StartAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            await flows.RecordResultAsync(
                Principal.LocalOwner, Project, flow.Id, "worktree", SoftwareActions.Worktree, JsonSerializer.SerializeToElement(new { verdict = "done" }), None);
            await flows.StartStepAsync(Principal.LocalOwner, Project, flow.Id, "build", None);
            await flows.SpawnedAsync(Principal.LocalOwner, Project, flow.Id, "build", ShippedAgents.Build.Hash, ShippedAgents.Build.Model, None);
            flowId = flow.Id;
        }

        ScriptedExecutor executor = Scripts.CleanBuildExecutor();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        using var client = new DaemonClient(temp.Home);
        FlowView paused = await Flowing.UntilAsync(client, Project, flowId, "paused");

        paused.PauseReason.Should().Be(
            "Step 'build' lost the session agent 'build' was launched in across a daemon restart (a session the daemon stopped before it was recorded), so its usage was not observed.");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("build"));
        executor.Spawns.Should().BeEmpty();

        // The resume counts its spawn like any other resume, so the cap is reached by the same count.
        await Flowing.ResumeAsync(client, Project, flowId, null);
        FlowView ended = await Flowing.UntilAsync(client, Project, flowId, "ended");
        ended.Spawns.Should().Be(4);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_start_stops_every_held_session_no_running_flow_waits_on_and_reattaches_the_one_a_flow_does()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Swept");
        SessionHandle orphan = await stalled.Inner.SpawnAsync(
            ShippedAgents.Build, "Nobody waits on this.", temp.Home.FlowWorkPath(Project, Ids.New()), new SpawnCaps(1m, TimeSpan.FromMinutes(1)), AnswerSchemas.Build, null, null, None);

        stalled.Open();
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled }, None);
        using var again = new DaemonClient(temp.Home);
        FlowView ended = await Flowing.UntilAsync(again, Project, flowId, "ended");

        ended.Steps.Select(s => s.Step).Should().Equal(CleanPath);
        stalled.Inner.Stops[0].Should().Be(orphan, "the unclaimed session is stopped before any flow advances");
        stalled.Inner.Stops.Should().ContainSingle(handle => handle == orphan);
        stalled.Inner.Stops.Should().ContainSingle(handle => handle == issued);
        stalled.Inner.Spawns.Where(s => s.Agent == "build").Select(s => s.Handle).Should().Equal(issued, orphan);
        ended.Steps.Single(s => s.Step == "build").Measurement.Should().Be(Scripts.Usage(1));

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_reattached_collect_that_throws_a_task_cancellation_of_its_own_pauses_and_stops_the_session_once()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Gave up after the restart");

        var givingUp = new GatedExecutor(stalled.Inner) { CollectsGiveUp = true };
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = givingUp }, None);
        using var again = new DaemonClient(temp.Home);
        FlowView paused = await Flowing.UntilAsync(again, Project, flowId, "paused");

        paused.PauseReason.Should().StartWith("Step 'build' got no judgment from agent 'build':").And.Contain("gave up collecting");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("build"));
        givingUp.Inner.Stops.Should().Equal(issued);
        givingUp.StopTokensCancelled.Should().Equal(false);
        givingUp.Inner.Spawns.Should().ContainSingle();

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_daemon_stop_during_a_reattached_collect_leaves_the_session_held_and_the_next_start_reattaches_it_again()
    {
        using var temp = new TemporaryHome();
        var stalled = new GatedExecutor(Scripts.CleanBuildExecutor());
        (Guid flowId, SessionHandle issued) = await StopWithSessionLeftAsync(temp, stalled, "Restarted twice");

        var heldAgain = new GatedExecutor(stalled.Inner);
        await using (RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = heldAgain }, None))
        {
            await heldAgain.Holding.WaitAsync(TimeSpan.FromSeconds(15));
            await second.StopAsync(None);
        }

        heldAgain.Inner.Stops.Should().BeEmpty("a daemon stop during a reattached collect leaves the session held");
        (await heldAgain.ListAsync(None)).Should().Equal(issued);
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            Flow? between = await new FlowService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowId, None);
            between?.Status.Should().Be(FlowStatus.Running);
            between?.Current?.SessionId.Should().Be(issued.Id);
        }

        var answering = new GatedExecutor(stalled.Inner);
        answering.Open();
        await using RunningDaemon third = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = answering }, None);
        using var client = new DaemonClient(temp.Home);
        FlowView ended = await Flowing.UntilAsync(client, Project, flowId, "ended");

        ended.Steps.Select(s => s.Step).Should().Equal(CleanPath);
        ended.Spawns.Should().Be(3);
        answering.Inner.Spawns.Where(s => s.Agent == "build").Should().ContainSingle();
        answering.Inner.Stops.Should().ContainSingle(handle => handle == issued);

        await third.StopAsync(None);
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
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Will not stop");
        Guid flowId = (await Flowing.BoundAsync(client, Project, parcel.Id)).Id;
        (await Flowing.UntilAsync(client, Project, flowId, "paused")).PauseReason.Should().StartWith("Agent 'build' gave no valid 'build' answer");
        SessionHandle issued = stubborn.Inner.Spawns.Should().ContainSingle().Subject.Handle;
        (await stubborn.ListAsync(None)).Should().ContainSingle("its stop after the pause failed").Which.Should().Be(issued);

        await Flowing.ResumeAsync(client, Project, flowId, "Again.");
        await Flowing.UntilAsync(async () => (await Flowing.ShowAsync(client, Project, flowId)).PauseReason?.Contains("could not stop", StringComparison.Ordinal) == true, "the resume pauses on the stop");
        FlowView paused = await Flowing.ShowAsync(client, Project, flowId);

        paused.PauseReason.Should().StartWith($"Step 'build' could not stop session {Ids.Short(issued.Id)} that agent 'build' was left running in");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("build"));
        stubborn.Inner.Spawns.Should().ContainSingle("no second agent is launched beside one that would not stop");

        await daemon.StopAsync(None);
    }

    /// <summary>
    /// Starts a daemon on the gated executor, waits until the bound flow's judgment has recorded its
    /// session and is held in its collect, and stops the daemon, checking that the session was left
    /// alive and the flow names it.
    /// </summary>
    internal static async Task<(Guid FlowId, SessionHandle Issued)> StopWithSessionLeftAsync(TemporaryHome temp, GatedExecutor stalled, string title)
    {
        Guid flowId;
        Guid sessionId = Guid.Empty;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = stalled }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            using var client = new DaemonClient(temp.Home);
            FlowService flows = first.Services.GetRequiredService<FlowService>();
            ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, title);
            flowId = (await Flowing.BoundAsync(client, Project, parcel.Id)).Id;
            await stalled.Holding.WaitAsync(TimeSpan.FromSeconds(15));
            await Flowing.UntilAsync(
                async () =>
                {
                    Flow? flow = await flows.ShowAsync(Principal.LocalOwner, Project, flowId, None);
                    sessionId = flow?.Current?.SessionId ?? Guid.Empty;
                    return sessionId != Guid.Empty;
                },
                "the flow names its judgment's session");
            await first.StopAsync(None);
        }

        SessionHandle issued = stalled.Inner.Spawns.Should().ContainSingle().Subject.Handle;
        issued.Id.Should().Be(sessionId);
        stalled.Inner.Stops.Should().BeEmpty("a daemon stop leaves a recorded session for a reattach");
        stalled.StopTokensCancelled.Should().BeEmpty();
        (await stalled.ListAsync(None)).Should().Equal(issued);

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            Flow? between = await new FlowService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowId, None);
            between.Should().NotBeNull();
            between.Status.Should().Be(FlowStatus.Running, "a daemon stop is not a pause");
            between.Current?.IsRecorded.Should().BeFalse();
            between.Current?.SessionId.Should().Be(issued.Id);
            between.Current?.AwaitsLaunchedSession.Should().BeTrue();
        }

        return (flowId, issued);
    }

    private static async Task<DateTimeOffset> LaunchedAtAsync(TemporaryHome temp, Guid flowId)
    {
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        Flow? flow = await new FlowService(store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Project, flowId, None);
        return flow?.Current?.SpawnedAt ?? throw new InvalidOperationException("The flow's visit recorded no launch.");
    }

    private static async Task PublishAgentAsync(RunningDaemon daemon, AgentDefinition agent)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Agent, DefinitionScope.Project, Project, agent.Name, AgentDefinition.CanonicalJson(agent), agent.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        Definition? resolved = await definitions.ResolveAgentAsync(Principal.LocalOwner, Project, agent.Name, None);
        resolved?.ContentHash.Should().Be(agent.Hash, "the republished agent is what resolution now returns");
    }

    private static async Task<IReadOnlyList<object>> EventsOfAsync(IDocumentStore store, Guid flowId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(flowId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
