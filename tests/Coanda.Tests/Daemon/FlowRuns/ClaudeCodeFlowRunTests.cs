using Coanda.Cli;
using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Tests.Daemon.Executors;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>Flow runs whose agent steps run on the Claude Code adapter, against the fake claude executable.</summary>
public sealed class ClaudeCodeFlowRunTests
{
    private const string Project = "alpha";
    private const string Valid = """{"verdict":"done","summary":"built"}""";
    private const decimal Budget = 3m;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task An_agent_step_with_no_valid_answer_after_its_retry_pauses_on_its_transcript_in_the_flow_runs_transcript_directory_and_a_resume_writes_the_record_again()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(
            Answer("I am not sure.", rewrite: new { path = ".coanda/task.md", content = "rewritten by the session" }),
            Answer("Still not sure."),
            Answer(Valid));
        await using RunningDaemon daemon = await StartAsync(temp, fake);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Try twice", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, (await PublishAsync(daemon, "build")).Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        string work = temp.Home.FlowRunWorkPath(Project, started.Id);
        string transcripts = temp.Home.FlowRunTranscriptPath(Project, started.Id);
        string transcript = Assert.Single(Directory.GetFiles(transcripts));
        Assert.StartsWith("Agent 'build' gave no valid 'build' answer after one retry", paused.PauseReason, StringComparison.Ordinal);
        Assert.Equal(transcript, paused.Lever);
        Assert.Equal(fake.Starts[0].After("--session-id"), Path.GetFileNameWithoutExtension(transcript));
        foreach (string input in new[] { "task.md", "prompt.md", "persona.md" })
        {
            Assert.True(File.Exists(Path.Combine(work, ".coanda", input)));
        }

        // the runner and the adapter write nothing else into the work directory, and no transcript
        Assert.Equal(3, Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Count());
        Assert.Equal("rewritten by the session", File.ReadAllText(Path.Combine(work, ".coanda", "task.md")));

        await FlowRunDriver.ResumeAsync(client, Project, started.Id, "Once more.");
        Assert.Equal("built", (await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended")).Outcome);
        string actual = File.ReadAllText(Path.Combine(work, ".coanda", "task.md"));
        Assert.StartsWith("# Try twice", actual, StringComparison.Ordinal);
        Assert.Contains($"- Parcel: {parcel.Id}", actual, StringComparison.Ordinal);
        Assert.Equal(ContextOf(fake.Starts[2].Input), File.ReadAllText(Path.Combine(work, ".coanda", "prompt.md")));
        // the resume's new spawn writes a transcript of its own
        Assert.Equal(2, Directory.GetFiles(transcripts).Length);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_attempt_its_spend_cap_stopped_is_not_retried_and_its_tokens_and_cost_reach_the_flow_run()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(new
        {
            result = "error",
            subtype = "error_max_budget_usd",
            reply = (string?)null,
            errors = new[] { "Reached maximum budget ($3)" },
            totalCost = 3.1m,
            turns = 5,
            durationMs = 20000,
            exitCode = 1,
            usage = new { input = 0, output = 0, cacheCreation = 0, cacheRead = 0 },
            sinceLast = new Dictionary<string, object> { ["claude-sonnet-5-5"] = new { input = 1, output = 2, cacheCreation = 3, cacheRead = 4, cost = 3.1m } },
        });
        await using RunningDaemon daemon = await StartAsync(temp, fake);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Over budget", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, (await PublishAsync(daemon, "build")).Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        Assert.Equal("Step 'build' got no judgment from agent 'build': The claude CLI ended the session's process with an error result (error_max_budget_usd): Reached maximum budget ($3)", paused.PauseReason);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Equal(new Measurement(10, 3.1m, 5, TimeSpan.FromSeconds(20)), paused.PauseUsage);
        Assert.Equal(3.1m, paused.Cost);
        // an error result is not retried
        Assert.Single(fake.Starts);
        Assert.Equal("3", fake.Starts[0].After("--max-budget-usd"));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_step_past_the_wall_clock_limit_during_its_retry_pauses_with_the_first_attempts_usage_and_what_the_retry_printed_but_no_cost_for_it()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(
            Answer("Thinking it over.", cost: 0.2m),
            new
            {
                result = "hang",
                messages = new object[]
                {
                    new { id = "msg_a", input = 1, output = 1, cacheCreation = 0, cacheRead = 50 },
                    new { id = "msg_a", input = 1, output = 9, cacheCreation = 0, cacheRead = 50 },
                    new { id = "msg_b", input = 2, output = 3, cacheCreation = 4, cacheRead = 60 },
                },
            });
        await using RunningDaemon daemon = await StartAsync(temp, fake, TimeSpan.FromSeconds(5));
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Slow retry", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, (await PublishAsync(daemon, "build")).Name);
        FlowRunView paused = await UntilPausedAsync(client, started.Id);

        Assert.Equal("Step 'build' ran past the wall-clock limit of 5 seconds for one agent step, so its session was stopped. "
            + "The session did not report the cost of its attempt in progress, so none is recorded for it; the spawn ran under a cost ceiling of $3.00.", paused.PauseReason);
        Measurement usage = paused.PauseUsage ?? throw new InvalidOperationException("The pause recorded no usage.");
        // msg_a printed twice is counted once, at its last figure
        Assert.Equal(100 + (1 + 9 + 0 + 50) + (2 + 3 + 4 + 60), usage.Tokens);
        // only the first attempt reported a cost
        Assert.Equal(0.2m, usage.CostUsd);
        Assert.Equal(2 + 2, usage.Turns);
        Assert.True(usage.Duration > TimeSpan.FromMilliseconds(1500), $"Expected more than 1.5 seconds, but found {usage.Duration}.");
        Assert.Equal(0.2m, paused.Cost);
        Assert.Equal(2, fake.Starts.Count);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_daemon_stop_leaves_a_running_session_whose_answer_and_usage_the_next_daemon_records_with_a_new_executor()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(Answer(Valid, cost: 0.6m, waitFor: "release"));
        (Guid flowRunId, int pid) = await StopWhileRunningAsync(temp, fake);

        Assert.True(FakeClaude.Running(pid), "the daemon's stop leaves the session's process running");
        fake.Release();
        FlowRunView ended = await RestartUntilAsync(temp, fake, flowRunId, "ended");

        Assert.Equal("built", ended.Outcome);
        Assert.Equal(new Measurement(100, 0.6m, 2, TimeSpan.FromMilliseconds(1500)), Assert.Single(ended.Steps).Measurement);
        Assert.Equal(0.6m, ended.Cost);
        Assert.Equal(1, ended.Spawns);
        // the next daemon starts no process for a session it reattaches
        Assert.Single(fake.Starts);
    }

    [Fact]
    public async Task A_session_that_answered_while_no_daemon_ran_is_recorded_by_the_next_daemon()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(Answer(Valid, cost: 0.6m, waitFor: "release"));
        (Guid flowRunId, int pid) = await StopWhileRunningAsync(temp, fake);

        fake.Release();
        await FlowRunDriver.UntilAsync(() => FakeClaude.Finished(pid), "the session's process has answered and exited");
        FlowRunView ended = await RestartUntilAsync(temp, fake, flowRunId, "ended");

        Assert.Equal(new Measurement(100, 0.6m, 2, TimeSpan.FromMilliseconds(1500)), Assert.Single(ended.Steps).Measurement);
        Assert.Single(fake.Starts);
    }

    [Fact]
    public async Task A_reattached_session_whose_first_answer_is_invalid_is_resumed_once_and_recorded_with_both_turns_usage()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(Answer("I am not sure.", cost: 0.5m, waitFor: "release"), Answer(Valid, cost: 0.25m));
        (Guid flowRunId, int _) = await StopWhileRunningAsync(temp, fake);

        fake.Release();
        FlowRunView ended = await RestartUntilAsync(temp, fake, flowRunId, "ended");

        Assert.Equal(new Measurement(200, 0.75m, 4, TimeSpan.FromMilliseconds(3000)), Assert.Single(ended.Steps).Measurement);
        Assert.Equal(2, fake.Starts.Count);
        Assert.Equal(fake.Starts[0].After("--session-id"), fake.Starts[1].After("--resume"));
    }

    [Fact]
    public async Task A_session_whose_process_exited_without_a_result_while_no_daemon_ran_pauses_its_flow_run_as_lost()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(new { result = "none", waitFor = "release", exitCode = 1 });
        (Guid flowRunId, int pid) = await StopWhileRunningAsync(temp, fake);

        fake.Release();
        await FlowRunDriver.UntilAsync(() => FakeClaude.Finished(pid), "the session's process has exited");
        FlowRunView paused = await RestartUntilAsync(temp, fake, flowRunId, "paused");

        Assert.StartsWith("Step 'build' lost the session agent 'build' was launched in across a daemon restart", paused.PauseReason, StringComparison.Ordinal);
        Assert.EndsWith("so its usage was not observed.", paused.PauseReason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        Assert.Single(fake.Starts);
    }

    [Fact]
    public async Task A_spawn_whose_cli_skipped_its_settings_document_is_stopped_while_it_runs_and_its_agent_step_pauses_saying_so()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(new { settings = "skipped", version = "2.1.400", result = "hang" });
        await using RunningDaemon daemon = await StartAsync(temp, fake);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Unguarded spawn", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, (await PublishAsync(daemon, "build")).Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        Assert.StartsWith(NotApplied("2.1.400"), paused.PauseReason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("build"), paused.Lever);
        // the stop reports what a process stopped while it ran may have spent
        Assert.NotNull(paused.PauseUsage);
        int pid = Assert.Single(fake.Starts).Pid;
        await FlowRunDriver.UntilAsync(() => !FakeClaude.Running(pid), "the session's process was stopped, though the fake would have run on");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_whose_cli_skipped_its_settings_document_is_stopped_while_it_runs_and_its_agent_step_pauses_saying_so()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(Answer("I am not sure.", cost: 0.2m), new { settings = "skipped", result = "hang" });
        await using RunningDaemon daemon = await StartAsync(temp, fake);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Unguarded resume", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, (await PublishAsync(daemon, "build")).Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        Assert.StartsWith(NotApplied("2.1.318"), paused.PauseReason, StringComparison.Ordinal);
        // the spawn's collected attempt still counts
        Assert.Equal(0.2m, paused.PauseUsage?.CostUsd);
        Assert.Equal(2, fake.Starts.Count);
        Assert.Equal(fake.Starts[0].After("--session-id"), fake.Starts[1].After("--resume"));
        await FlowRunDriver.UntilAsync(() => !FakeClaude.Running(fake.Starts[1].Pid), "the resumed process was stopped, though the fake would have run on");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_session_the_next_daemon_reattaches_whose_cli_skipped_its_settings_document_is_stopped_and_its_agent_step_pauses_saying_so()
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(new { settings = "skipped", holdInit = "release", result = "hang" });
        (Guid flowRunId, int pid) = await StopWhileRunningAsync(temp, fake);

        Assert.True(FakeClaude.Running(pid), "the first daemon saw no init event, so it left the session running for a reattach");
        fake.Release();
        FlowRunView paused = await RestartUntilAsync(temp, fake, flowRunId, "paused");

        Assert.StartsWith(NotApplied("2.1.318"), paused.PauseReason, StringComparison.Ordinal);
        Assert.Single(fake.Starts);
        await FlowRunDriver.UntilAsync(() => FakeClaude.Finished(pid), "the reattached process was stopped, though the fake would have run on");
    }

    [Theory]
    [InlineData("codex", "gpt-5", "high", "Agent 'outsider' names the vendor 'codex', and this executor runs only agents of the vendor 'claude-code'.")]
    [InlineData("claude-code", "--dangerously-skip-permissions", "high", "Agent 'outsider' names the model '--dangerously-skip-permissions', which is not a plain token")]
    [InlineData("claude-code", "claude-sonnet-5-5", "high --verbose", "Agent 'outsider' names the effort 'high --verbose', which is not a plain token")]
    public async Task An_agent_the_adapter_cannot_run_pauses_its_flow_run_before_any_process_starts(string vendor, string model, string effort, string rule)
    {
        using var temp = new TemporaryHome();
        using var fake = new FakeClaude(Answer(Valid));
        await using RunningDaemon daemon = await StartAsync(temp, fake);
        using var client = new DaemonClient(temp.Home);
        AgentDefinition outsider = SoftwareAgents.Build with { Name = "outsider", Model = new ModelSpec(vendor, model, effort) };
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Agent, DefinitionScope.Project, Project, outsider.Name, AgentDefinition.CanonicalJson(outsider), outsider.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Cannot run", null), None);

        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, (await PublishAsync(daemon, outsider.Name)).Name);
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        Assert.StartsWith($"Step 'build' got no judgment from agent 'outsider': {rule}", paused.PauseReason, StringComparison.Ordinal);
        Assert.Empty(fake.Starts);

        await daemon.StopAsync(None);
    }

    private static object Answer(string reply, decimal cost = 0.1m, object? rewrite = null, string? waitFor = null) => new
    {
        reply,
        waitFor,
        totalCost = cost,
        turns = 2,
        durationMs = 1500,
        usage = new { input = 10, output = 20, cacheCreation = 30, cacheRead = 40 },
        sinceLast = new Dictionary<string, object> { ["claude-sonnet-5-5"] = new { input = 10, output = 20, cacheCreation = 30, cacheRead = 40, cost } },
        rewrite,
    };

    private static string NotApplied(string version) =>
        $"Step 'build' got no judgment from agent 'build': The session's settings document, and with it its tool guard, was not applied by claude CLI {version}: "
        + "its init event came without the evidence the document's SessionStart hook prints, so its process was stopped and no answer was collected from it. "
        + "A managed settings policy that turns hooks off suppresses that evidence too, and is refused the same way.";

    /// <summary>The context a prompt opens with: everything before the paragraph that names the files.</summary>
    private static string ContextOf(string prompt) => prompt[..prompt.IndexOf("\n\nThe task's record is in", StringComparison.Ordinal)];

    private static async Task<RunningDaemon> StartAsync(TemporaryHome temp, FakeClaude fake, TimeSpan? limit = null)
    {
        var options = new DaemonOptions { Executor = fake.Executor(temp.Home), AgentStepLimit = limit ?? DaemonOptions.DefaultAgentStepLimit };
        RunningDaemon daemon = await Daemons.StartAsync(temp.Home, options, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        return daemon;
    }

    private static Task<Flow> PublishAsync(RunningDaemon daemon, string agent) => Publishing.FlowAsync(daemon, Project, new Flow
    {
        Name = $"judge-with-{agent}",
        Accepts = ["task"],
        Outcome = "built",
        Budget = Budget,
        SpawnCap = 2,
        Steps =
        [
            new FlowStep
            {
                Name = "build",
                Agent = agent,
                AnswerSchema = "build",
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    });

    /// <summary>
    /// Starts a daemon on the fake, starts a flow run whose agent step's session is held running by the
    /// fake, and stops the daemon once the flow run names the session, returning the flow run and the
    /// session's process id.
    /// </summary>
    private static async Task<(Guid FlowRunId, int Pid)> StopWhileRunningAsync(TemporaryHome temp, FakeClaude fake)
    {
        Guid flowRunId;
        await using (RunningDaemon first = await StartAsync(temp, fake))
        {
            using var client = new DaemonClient(temp.Home);
            FlowRunService flowRuns = first.Services.GetRequiredService<FlowRunService>();
            ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Across a restart", null), None);
            flowRunId = (await FlowRunDriver.StartAsync(client, Project, parcel.Id, (await PublishAsync(first, "build")).Name)).Id;
            await FlowRunDriver.UntilAsync(
                async () => fake.Starts.Count == 1
                    && (await flowRuns.ShowAsync(Principal.LocalOwner, Project, flowRunId, None))?.Current?.SessionId is not null,
                "the flow run names its running session");
            await first.StopAsync(None);
        }

        return (flowRunId, fake.Starts[0].Pid);
    }

    /// <summary>Starts a second daemon on the same home with a new executor, which holds nothing from the first, and waits for the flow run's status.</summary>
    private static async Task<FlowRunView> RestartUntilAsync(TemporaryHome temp, FakeClaude fake, Guid flowRunId, string status)
    {
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = fake.Executor(temp.Home) }, None);
        using var client = new DaemonClient(temp.Home);
        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, flowRunId, status);
        await second.StopAsync(None);
        return flowRun;
    }

    /// <summary>Waits past the wall-clock limit, which is longer than the shared patience of <see cref="FlowRunDriver"/>.</summary>
    private static async Task<FlowRunView> UntilPausedAsync(DaemonClient client, Guid flowRunId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        FlowRunView flowRun = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
        while (flowRun.Status != "paused" && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, None);
            flowRun = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
        }

        return flowRun;
    }
}
