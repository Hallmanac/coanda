using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Flows.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Fisher;
using Fisher.Services;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>
/// A person's pause, resume, or stop is not refused only because the runner wrote to the same flow
/// between the operation's read and its save. Each test forces that order with a session listener
/// that makes the runner's write from inside the operation's save, before its version check.
/// </summary>
public sealed class FlowControlConcurrencyTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Runner = new("flow:runner", Role.Agent);

    [Fact]
    public async Task A_stop_whose_read_the_runners_session_record_overtakes_reads_again_and_stops_the_flow()
    {
        using var temp = new TemporaryHome();
        var interjection = new Interjection();
        await using IDocumentStore store = await OpenAsync(temp, interjection);
        var flows = new FlowService(store, TimeProvider.System);
        Flow flow = await LaunchedBuildAsync(store, flows);
        Guid session = Ids.New();

        interjection.Arm(() => flows.SessionOpenedAsync(Runner, Project, flow.Id, "build", session, None));
        Flow stopped = await flows.StopAsync(Principal.LocalOwner, Project, flow.Id, "Not wanted.", None);

        interjection.Fired.Should().Be(1, "the session record landed inside the stop's save");
        stopped.Status.Should().Be(FlowStatus.Stopped);
        stopped.StopReason.Should().Be("Not wanted.");
        stopped.Current?.SessionId.Should().Be(session, "the stop read the flow again, the session record included");
        (await EventsAsync(store, flow.Id)).Should().EndWith([typeof(JudgmentSessionOpened), typeof(FlowStopped)]);
    }

    [Fact]
    public async Task A_pause_whose_read_the_runners_session_record_overtakes_reads_again_and_pauses_the_flow()
    {
        using var temp = new TemporaryHome();
        var interjection = new Interjection();
        await using IDocumentStore store = await OpenAsync(temp, interjection);
        var flows = new FlowService(store, TimeProvider.System);
        Flow flow = await LaunchedBuildAsync(store, flows);
        Guid session = Ids.New();

        interjection.Arm(() => flows.SessionOpenedAsync(Runner, Project, flow.Id, "build", session, None));
        Flow paused = await flows.PauseAsync(Principal.LocalOwner, Project, flow.Id, "Hold on.", FlowLevers.PerformAgain("build"), None);

        interjection.Fired.Should().Be(1, "the session record landed inside the pause's save");
        paused.Status.Should().Be(FlowStatus.Paused);
        paused.PauseReason.Should().Be("Hold on.");
        paused.Current?.SessionId.Should().Be(session, "the pause read the flow again, the session record included");
        (await EventsAsync(store, flow.Id)).Should().EndWith([typeof(JudgmentSessionOpened), typeof(FlowPaused)]);
    }

    [Fact]
    public async Task A_resume_whose_read_a_dropped_judgments_usage_record_overtakes_reads_again_and_resumes_the_flow()
    {
        using var temp = new TemporaryHome();
        var interjection = new Interjection();
        await using IDocumentStore store = await OpenAsync(temp, interjection);
        var flows = new FlowService(store, TimeProvider.System);
        Flow flow = await LaunchedBuildAsync(store, flows);
        Guid session = Ids.New();
        await flows.SessionOpenedAsync(Runner, Project, flow.Id, "build", session, None);
        await flows.PauseAsync(Principal.LocalOwner, Project, flow.Id, "Hold on.", FlowLevers.PerformAgain("build"), None);

        interjection.Arm(() => flows.RecordUsageAsync(Runner, Project, flow.Id, "build", "build", session, Scripts.Usage(1), None));
        Flow resumed = await flows.ResumeAsync(Principal.LocalOwner, Project, flow.Id, "Go on.", None);

        interjection.Fired.Should().Be(1, "the usage record landed inside the resume's save");
        resumed.Status.Should().Be(FlowStatus.Running);
        resumed.Cost.Should().Be(Scripts.Usage(1).CostUsd, "the resume read the flow again, the usage record included");
        (await EventsAsync(store, flow.Id)).Should().EndWith([typeof(JudgmentUsageRecorded), typeof(FlowResumed)]);
    }

    /// <summary>Opens a seeded store whose every save runs <paramref name="interjection"/> first.</summary>
    private static async Task<IDocumentStore> OpenAsync(TemporaryHome temp, Interjection interjection)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            StoreConfiguration.Configure(options, temp.Home.DatabasePath);
            options.Listeners.Add(interjection);
        });
        await new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names).SeedAsync(ShippedDefinitions.All, None);
        await RegisteredProjects.AddAsync(store, Project, None);
        return store;
    }

    /// <summary>A build flow at its judgment step, with the launch recorded and no session yet, as the runner leaves it just before the spawn returns.</summary>
    private static async Task<Flow> LaunchedBuildAsync(IDocumentStore store, FlowService flows)
    {
        var parcels = new ParcelService(store, TimeProvider.System);
        var parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Raced", null, None);
        Flow flow = await flows.StartAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
        await flows.RecordResultAsync(
            Runner, Project, flow.Id, "worktree", SoftwareActions.Worktree, JsonSerializer.SerializeToElement(new { verdict = "done" }), None);
        await flows.StartStepAsync(Runner, Project, flow.Id, "build", None);
        return await flows.SpawnedAsync(Runner, Project, flow.Id, "build", ShippedAgents.Build.Hash, ShippedAgents.Build.Model, None);
    }

    private static async Task<IReadOnlyList<Type>> EventsAsync(IDocumentStore store, Guid flowId)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        var events = await session.Events.FetchStreamAsync(flowId, token: None);
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
