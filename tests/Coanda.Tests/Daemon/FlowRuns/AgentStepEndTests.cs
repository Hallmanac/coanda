using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A person's pause or stop ends the agent step in progress through the executor, every agent step that
/// launched a session counts what it spent once in its flow run's cost, and an answer from a session a
/// resume has moved past is never recorded.
/// </summary>
public sealed class AgentStepEndTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Patience = TestPatience.Bound;

    /// <summary>What the test double reports for an attempt in progress when its session is stopped.</summary>
    private static readonly Measurement InProgress = new(400, 0.1m, 2, TimeSpan.FromSeconds(12));

    [Fact]
    public async Task A_pause_while_an_agent_step_runs_stops_its_session_without_waiting_for_an_answer_and_its_worker_finishes()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 1)));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IFlowRunAdvancer advancer = daemon.Services.GetRequiredService<IFlowRunAdvancer>();
        FlowRunView started = await StartSingleAsync(daemon, client, "Paused mid-agent-step");
        await executor.Collecting.WaitAsync(Patience);

        // A refused pause or stop leaves the agent step running.
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => FlowRunDriver.PauseAsync(client, Project, started.Id, " "));
        Assert.Equal("A pause records why the flow run stopped; give a reason.", exception.Message);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => FlowRunDriver.StopAsync(client, Project, started.Id, ""));
        Assert.Equal("Stopping a flow run records why; give a reason.", exception2.Message);
        await Task.Delay(TimeSpan.FromMilliseconds(200), None);
        // a refused pause or stop does not end the agent step
        Assert.Empty(executor.Inner.Stops);

        await FlowRunDriver.PauseAsync(client, Project, started.Id, "Waiting on the API owner.");
        await advancer.RequestAdvance(Project, started.Id).WaitAsync(Patience);

        SessionHandle held = Assert.Single(executor.Inner.Spawns).Handle;
        Assert.Equal([held], executor.Inner.Stops);
        FlowRunView paused = await FlowRunDriver.ShowAsync(client, Project, started.Id);
        Assert.Equal("paused", paused.Status);
        Assert.Equal("Waiting on the API owner.", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Null(Assert.Single(paused.Steps).Answer);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_stop_while_an_agent_step_runs_stops_its_session_without_waiting_for_an_answer_and_its_worker_finishes()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 1)));
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IFlowRunAdvancer advancer = daemon.Services.GetRequiredService<IFlowRunAdvancer>();
        FlowRunView started = await StartSingleAsync(daemon, client, "Stopped mid-agent-step");
        await executor.Collecting.WaitAsync(Patience);

        await FlowRunDriver.StopAsync(client, Project, started.Id, "Not needed any more.");
        await advancer.RequestAdvance(Project, started.Id).WaitAsync(Patience);

        SessionHandle held = Assert.Single(executor.Inner.Spawns).Handle;
        Assert.Equal([held], executor.Inner.Stops);
        FlowRunView stopped = await FlowRunDriver.ShowAsync(client, Project, started.Id);
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal("Not needed any more.", stopped.StopReason);
        Assert.Null(Assert.Single(stopped.Steps).Answer);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_step_a_pause_ended_counts_what_its_attempt_in_progress_reported_beside_the_resumed_answers_cost()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 2))) { InProgressUsage = InProgress };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IFlowRunAdvancer advancer = daemon.Services.GetRequiredService<IFlowRunAdvancer>();
        FlowRunView started = await StartSingleAsync(daemon, client, "Paused and resumed");
        await executor.Collecting.WaitAsync(Patience);

        await FlowRunDriver.PauseAsync(client, Project, started.Id, "Hold on.");
        await advancer.RequestAdvance(Project, started.Id).WaitAsync(Patience);
        FlowRunView paused = await FlowRunDriver.ShowAsync(client, Project, started.Id);
        // the stopped attempt's reported usage is recorded
        Assert.Equal(InProgress.CostUsd, paused.Cost);
        Assert.Equal("Hold on.", paused.PauseReason);
        // the usage record leaves the person's pause as it was
        Assert.Null(paused.PauseUsage);

        executor.Open();
        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Go on.");
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        Assert.Equal(InProgress.CostUsd + Scripts.Usage(2).CostUsd, ended.Cost);
        Assert.Equal(Scripts.Usage(2), Assert.Single(ended.Steps).Measurement);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_step_a_stop_ended_counts_what_its_attempt_in_progress_reported()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 1))) { InProgressUsage = InProgress };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IFlowRunAdvancer advancer = daemon.Services.GetRequiredService<IFlowRunAdvancer>();
        FlowRunView started = await StartSingleAsync(daemon, client, "Stopped and counted");
        await executor.Collecting.WaitAsync(Patience);

        await FlowRunDriver.StopAsync(client, Project, started.Id, "Not needed.");
        await advancer.RequestAdvance(Project, started.Id).WaitAsync(Patience);

        FlowRunView stopped = await FlowRunDriver.ShowAsync(client, Project, started.Id);
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal(InProgress.CostUsd, stopped.Cost);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_step_a_pause_ended_during_its_retry_counts_its_first_attempt_when_the_executor_reports_nothing_in_progress()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("maybe", 1), Scripts.Answer("done", 2))) { FreeCollects = 1 };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IFlowRunAdvancer advancer = daemon.Services.GetRequiredService<IFlowRunAdvancer>();
        FlowRunView started = await StartSingleAsync(daemon, client, "Paused mid-retry");
        await executor.Holding.WaitAsync(Patience);
        // the invalid first answer was retried in the same session
        Assert.Single(executor.Inner.Resumes);

        await FlowRunDriver.PauseAsync(client, Project, started.Id, "Hold on.");
        await advancer.RequestAdvance(Project, started.Id).WaitAsync(Patience);

        FlowRunView paused = await FlowRunDriver.ShowAsync(client, Project, started.Id);
        Assert.Equal("paused", paused.Status);
        Assert.Equal(Scripts.Usage(1).CostUsd, paused.Cost);
        Assert.Single(executor.Inner.Stops);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_step_whose_collect_fails_of_its_own_accord_pauses_and_counts_what_its_attempt_reported()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 1))) { CollectsGiveUp = true, InProgressUsage = InProgress };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        FlowRunView started = await StartSingleAsync(daemon, client, "Gave up");

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        Assert.StartsWith("Step 'build' got no answer from agent 'build':", paused.PauseReason, StringComparison.Ordinal);
        Assert.Equal(InProgress, paused.PauseUsage);
        Assert.Equal(InProgress.CostUsd, paused.Cost);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_answer_from_a_session_a_resume_moved_past_is_refused_without_a_pause_and_counts_once_beside_the_new_spawns_answer()
    {
        using var temp = new TemporaryHome();
        var executor = new GatedExecutor(BuildScript(Scripts.Answer("done", 1), Scripts.Answer("done", 2)))
        {
            HeldCollectsIgnoreCancellation = true,
        };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        FlowRunView started = await StartSingleAsync(daemon, client, "Answered late");
        await executor.Holding.WaitAsync(Patience);

        await FlowRunDriver.PauseAsync(client, Project, started.Id, "Hold on.");
        Assert.Equal("running", (await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Go on.")).Status);
        // the held collect has not returned, so nothing performed the step again yet
        Assert.Single(executor.Inner.Spawns);
        executor.Open();

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        IReadOnlyList<ScriptedSpawn> spawns = executor.Inner.Spawns;
        // a second spawn performs the resumed step
        Assert.Equal(2, spawns.Count);
        FlowRunStepView build = Assert.Single(ended.Steps);
        // only the second spawn's answer is recorded
        Assert.Equal("answer 2", build.Answer.GetValueOrDefault().GetProperty("summary").GetString());
        Assert.Equal(Scripts.Usage(2), build.Measurement);
        // the refused answer counts once
        Assert.Equal(Scripts.Usage(1).CostUsd + Scripts.Usage(2).CostUsd, ended.Cost);

        IReadOnlyList<object> events = await EventsAsync(daemon, started.Id);
        // the refused answer does not pause the flow run
        Assert.Equal("Hold on.", Assert.Single(events.OfType<FlowRunPaused>()).Reason);
        AgentStepUsageRecorded dropped = Assert.Single(events.OfType<AgentStepUsageRecorded>());
        Assert.Equal(spawns[0].Handle.Id, dropped.SessionId);
        Assert.Equal(Scripts.Usage(1), dropped.Usage);
        Assert.Single(events.OfType<AgentStepAnswered>());

        await daemon.StopAsync(None);
    }

    private static ScriptedExecutor BuildScript(params ScriptEntry[] entries) =>
        new(new Dictionary<string, IReadOnlyList<ScriptEntry>> { ["build"] = entries });

    /// <summary>Publishes a flow of one agent step that may spawn twice, and starts it through the API on a new task.</summary>
    private static async Task<FlowRunView> StartSingleAsync(RunningDaemon daemon, DaemonClient client, string title)
    {
        Flow single = await Publishing.FlowAsync(daemon, Project, new Flow
        {
            Name = "one-agent-step",
            Accepts = ["task"],
            Outcome = "built",
            Budget = 10m,
            SpawnCap = 2,
            Steps =
            [
                new FlowStep
                {
                    Name = "build",
                    Agent = "build",
                    AnswerSchema = "build",
                    Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
                },
            ],
        });
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", title, null), None);
        return await FlowRunDriver.StartAsync(client, Project, parcel.Id, single.Name);
    }

    private static async Task<IReadOnlyList<object>> EventsAsync(RunningDaemon daemon, Guid flowRunId)
    {
        await using IDocumentSession session = daemon.Services.GetRequiredService<IDocumentStore>().LightweightSession(Project);
        var events = await session.Events.FetchStreamAsync(flowRunId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
