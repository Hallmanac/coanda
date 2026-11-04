using System.Text.Json;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Fisher;
using Fisher.Services;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A person's pause, resume, or stop is not refused only because the runner wrote to the same flow run
/// between the operation's read and its save. Each test forces that order with a session listener
/// that makes the runner's write from inside the operation's save, before its version check.
/// </summary>
public sealed class FlowRunControlConcurrencyTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Runner = new("flow-run:runner", Role.Agent);

    [Fact]
    public async Task A_stop_whose_read_the_runners_session_record_overtakes_reads_again_and_stops_the_flow_run()
    {
        using var temp = new TemporaryHome();
        var interjection = new Interjection();
        await using IDocumentStore store = await OpenAsync(temp, interjection);
        var flowRuns = new FlowRunService(store, TimeProvider.System);
        FlowRun flowRun = await LaunchedBuildAsync(store, flowRuns);
        Guid session = Ids.New();

        interjection.Arm(() => flowRuns.SessionOpenedAsync(Runner, Project, flowRun.Id, "build", session, None));
        FlowRun stopped = await flowRuns.StopAsync(Principal.LocalOwner, Project, flowRun.Id, "Not wanted.", None);

        interjection.Fired.Should().Be(1, "the session record landed inside the stop's save");
        stopped.Status.Should().Be(FlowRunStatus.Stopped);
        stopped.StopReason.Should().Be("Not wanted.");
        stopped.Current?.SessionId.Should().Be(session, "the stop read the flow run again, the session record included");
        (await EventsAsync(store, flowRun.Id)).Should().EndWith([typeof(AgentStepSessionOpened), typeof(FlowRunStopped)]);
    }

    [Fact]
    public async Task A_pause_whose_read_the_runners_session_record_overtakes_reads_again_and_pauses_the_flow_run()
    {
        using var temp = new TemporaryHome();
        var interjection = new Interjection();
        await using IDocumentStore store = await OpenAsync(temp, interjection);
        var flowRuns = new FlowRunService(store, TimeProvider.System);
        FlowRun flowRun = await LaunchedBuildAsync(store, flowRuns);
        Guid session = Ids.New();

        interjection.Arm(() => flowRuns.SessionOpenedAsync(Runner, Project, flowRun.Id, "build", session, None));
        FlowRun paused = await flowRuns.PauseAsync(Principal.LocalOwner, Project, flowRun.Id, "Hold on.", FlowRunLevers.PerformAgain("build"), None);

        interjection.Fired.Should().Be(1, "the session record landed inside the pause's save");
        paused.Status.Should().Be(FlowRunStatus.Paused);
        paused.PauseReason.Should().Be("Hold on.");
        paused.Current?.SessionId.Should().Be(session, "the pause read the flow run again, the session record included");
        (await EventsAsync(store, flowRun.Id)).Should().EndWith([typeof(AgentStepSessionOpened), typeof(FlowRunPaused)]);
    }

    [Fact]
    public async Task A_resume_whose_read_a_dropped_agent_steps_usage_record_overtakes_reads_again_and_resumes_the_flow_run()
    {
        using var temp = new TemporaryHome();
        var interjection = new Interjection();
        await using IDocumentStore store = await OpenAsync(temp, interjection);
        var flowRuns = new FlowRunService(store, TimeProvider.System);
        FlowRun flowRun = await LaunchedBuildAsync(store, flowRuns);
        Guid session = Ids.New();
        await flowRuns.SessionOpenedAsync(Runner, Project, flowRun.Id, "build", session, None);
        await flowRuns.PauseAsync(Principal.LocalOwner, Project, flowRun.Id, "Hold on.", FlowRunLevers.PerformAgain("build"), None);

        interjection.Arm(() => flowRuns.RecordUsageAsync(Runner, Project, flowRun.Id, "build", "build", session, Scripts.Usage(1), None));
        FlowRun resumed = await flowRuns.ResumeAsync(Principal.LocalOwner, Project, flowRun.Id, "Go on.", None);

        interjection.Fired.Should().Be(1, "the usage record landed inside the resume's save");
        resumed.Status.Should().Be(FlowRunStatus.Running);
        resumed.Cost.Should().Be(Scripts.Usage(1).CostUsd, "the resume read the flow run again, the usage record included");
        (await EventsAsync(store, flowRun.Id)).Should().EndWith([typeof(AgentStepUsageRecorded), typeof(FlowRunResumed)]);
    }

    /// <summary>Opens a seeded store whose every save runs <paramref name="interjection"/> first.</summary>
    private static async Task<IDocumentStore> OpenAsync(TemporaryHome temp, Interjection interjection)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            StoreConfiguration.Configure(options, temp.Home.DatabasePath);
            options.Listeners.Add(interjection);
        });
        await new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names).SeedAsync(ShippedDefinitions.All, None);
        await RegisteredProjects.AddAsync(store, Project, None);
        return store;
    }

    /// <summary>A build flow run at its agent step, with the launch recorded and no session yet, as the runner leaves it just before the spawn returns.</summary>
    private static async Task<FlowRun> LaunchedBuildAsync(IDocumentStore store, FlowRunService flowRuns)
    {
        var parcels = new ParcelService(store, TimeProvider.System);
        var parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Raced", null, None);
        FlowRun flowRun = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
        await flowRuns.RecordResultAsync(
            Runner, Project, flowRun.Id, "worktree", SoftwareSystemSteps.Worktree, JsonSerializer.SerializeToElement(new { verdict = "done" }), None);
        await flowRuns.StartStepAsync(Runner, Project, flowRun.Id, "build", None);
        return await flowRuns.SpawnedAsync(Runner, Project, flowRun.Id, "build", ShippedAgents.Build.Hash, ShippedAgents.Build.Model, None);
    }

    private static async Task<IReadOnlyList<Type>> EventsAsync(IDocumentStore store, Guid flowRunId)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        var events = await session.Events.FetchStreamAsync(flowRunId, token: None);
        return [.. events.Select(e => e.Data.GetType())];
    }

    /// <summary>
    /// Makes one armed write from inside the next save, before anything that save holds is written,
    /// so the write lands between that operation's read and its version check. It disarms as it
    /// fires, so the write's own save runs it no second time.
    /// </summary>
    private sealed class Interjection : IDocumentSessionListener
    {
        private Func<Task>? armed;
        private int fired;

        public int Fired => Volatile.Read(ref fired);

        public void Arm(Func<Task> write) => Volatile.Write(ref armed, write);

        public async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (Interlocked.Exchange(ref armed, null) is { } write)
            {
                await write();
                Interlocked.Increment(ref fired);
            }
        }

        public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token) => Task.CompletedTask;
    }
}
