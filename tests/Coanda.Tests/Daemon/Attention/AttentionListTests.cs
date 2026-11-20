using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Attention;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Transport;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon.Attention;

/// <summary>
/// The attention query over a store, with no daemon: every paused flow run this node started, across the
/// projects the principal may read or in the one it names, oldest pause first, each with the time of its
/// latest pause. Every flow run is written through <see cref="FlowRunService"/> on a clock the test moves.
/// </summary>
public sealed class AttentionListTests
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private static readonly Principal Owner = Principal.LocalOwner;
    private static readonly Principal Software = Principal.ForModule(SoftwareSteps.Module);

    /// <summary>An Agent-role principal with no session, as the trigger loop's is.</summary>
    private static readonly Principal Loop = new("coanda-trigger-loop", Role.Agent);

    private static readonly ModelSpec Model = new("anthropic", "claude-opus-5-5", "high");
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A clock that reads what the test last set.</summary>
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// The flow runs <see cref="SeedAsync"/> writes: in <see cref="Alpha"/>, <see cref="Late"/> paused at
    /// 09:30 on the parcel "Late", <see cref="Early"/> paused at 09:10 on "Early", one running, one ended, and
    /// one stopped; in <see cref="Beta"/>, <see cref="Middle"/> paused at 09:20 on "Middle".
    /// </summary>
    internal sealed record Seeded(Guid Early, Guid Middle, Guid Late, Guid Running, Guid Ended, Guid Stopped, Guid EarlyParcel);

    /// <summary>Registers both projects and writes the flow runs <see cref="Seeded"/> describes.</summary>
    internal static async Task<Seeded> SeedAsync(IDocumentStore store, Clock clock, bool withRunning = true)
    {
        await RegisteredProjects.AddAsync(store, Alpha, None);
        await RegisteredProjects.AddAsync(store, Beta, None);
        var parcels = new ParcelService(store, clock);
        var flowRuns = new FlowRunService(store, clock);

        Parcel lateParcel = await parcels.AddAsync(Owner, Alpha, "task", "Late", null, None);
        FlowRun late = await flowRuns.StartAsync(Owner, Alpha, lateParcel.Id, "build", None);

        Parcel earlyParcel = await parcels.AddAsync(Owner, Alpha, "task", "Early", null, None);
        FlowRun early = await LaunchedBuildAsync(flowRuns, Alpha, earlyParcel.Id);
        clock.Now = Start.AddMinutes(10);
        await flowRuns.PauseAsync(Owner, Alpha, early.Id, "Waiting on the API owner", "Resume it once they answer", None);

        Parcel middleParcel = await parcels.AddAsync(Owner, Beta, "task", "Middle", null, None);
        FlowRun middle = await flowRuns.StartAsync(Owner, Beta, middleParcel.Id, "build", None);
        clock.Now = Start.AddMinutes(20);
        await flowRuns.PauseAsync(Owner, Beta, middle.Id, "Held in beta", "Resume it", None);

        clock.Now = Start.AddMinutes(30);
        await flowRuns.PauseAsync(Owner, Alpha, late.Id, "Held late", "Resume it later", None);

        Guid running = Guid.Empty;
        if (withRunning)
        {
            Parcel runningParcel = await parcels.AddAsync(Owner, Alpha, "task", "Running", null, None);
            running = (await flowRuns.StartAsync(Owner, Alpha, runningParcel.Id, "build", None)).Id;
        }

        Parcel endedParcel = await parcels.AddAsync(Owner, Alpha, "task", "Ended", null, None);
        FlowRun ended = await flowRuns.StartAsync(Owner, Alpha, endedParcel.Id, "build", None);
        await flowRuns.PauseAsync(Owner, Alpha, ended.Id, "Paused before its end", "Resume it", None);
        await flowRuns.ResumeAsync(Owner, Alpha, ended.Id, null, null, None);
        await flowRuns.EndAsync(Owner, Alpha, ended.Id, SoftwareFlows.Build.Outcome, None);

        Parcel stoppedParcel = await parcels.AddAsync(Owner, Alpha, "task", "Stopped", null, None);
        FlowRun stopped = await flowRuns.StartAsync(Owner, Alpha, stoppedParcel.Id, "build", None);
        await flowRuns.PauseAsync(Owner, Alpha, stopped.Id, "Paused before its stop", "Resume it", None);
        await flowRuns.StopAsync(Owner, Alpha, stopped.Id, "Taken by hand.", None);

        return new Seeded(early.Id, middle.Id, late.Id, running, ended.Id, stopped.Id, earlyParcel.Id);
    }

    /// <summary>Starts a build on the parcel, runs its worktree step, and launches its build agent, leaving it running.</summary>
    internal static async Task<FlowRun> LaunchedBuildAsync(FlowRunService flowRuns, string project, Guid parcelId)
    {
        FlowRun flowRun = await flowRuns.StartAsync(Owner, project, parcelId, "build", None);
        await flowRuns.RecordResultAsync(Owner, project, flowRun.Id, "worktree", SoftwareSteps.Worktree, Json("""{"verdict":"done","path":"/tmp/wt","reused":false}"""), None);
        await flowRuns.StartStepAsync(Owner, project, flowRun.Id, "build", None);
        return await flowRuns.SpawnedAsync(Owner, project, flowRun.Id, "build", "agenthash", Model, None);
    }

    /// <summary>Gives every event of <paramref name="stream"/> in <paramref name="project"/> the record the import saves with another node's line.</summary>
    internal static async Task RecordPeerLinesAsync(IDocumentStore store, string project, Guid peer, Guid stream)
    {
        await using IDocumentSession session = store.LightweightSession(project);
        long sequence = 0;
        foreach (IEvent entry in await session.Events.FetchStreamAsync(stream, token: None))
        {
            session.Store(new LedgerLine { Id = entry.Id, Origin = peer, Sequence = ++sequence, Hash = $"sha256:{Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())}{new string('0', 32)}" });
        }

        await session.SaveChangesAsync(None);
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<Guid>> ListedAsync(AttentionService attention, Principal principal, string? project) =>
        [.. (await attention.ListAsync(principal, project, None)).Select(p => p.FlowRunId)];

    [Fact]
    public async Task With_no_project_named_every_paused_flow_run_of_every_project_is_listed_oldest_pause_first_with_its_fields()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        var clock = new Clock();
        Seeded seeded = await SeedAsync(store, clock);
        var attention = new AttentionService(store);

        IReadOnlyList<PausedFlowRun> paused = await attention.ListAsync(Owner, null, None);

        // The running, ended, and stopped flow runs are never listed, though the last two once paused.
        Assert.Equal([seeded.Early, seeded.Middle, seeded.Late], paused.Select(p => p.FlowRunId));
        Assert.Equal([Alpha, Beta, Alpha], paused.Select(p => p.Project));
        Assert.Equal([Start.AddMinutes(10), Start.AddMinutes(20), Start.AddMinutes(30)], paused.Select(p => p.PausedAt));

        PausedFlowRun early = paused[0];
        Assert.Equal(seeded.EarlyParcel, early.ParcelId);
        Assert.Equal("Early", early.ParcelTitle);
        Assert.Equal("build", early.FlowName);
        Assert.Equal("build", early.Step);
        Assert.Equal("Waiting on the API owner", early.Reason);
        Assert.Equal("Resume it once they answer", early.Lever);
        Assert.Equal("worktree", paused[2].Step);
    }

    [Fact]
    public async Task A_named_project_lists_only_its_own_and_an_unregistered_one_is_refused_naming_it()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        var clock = new Clock();
        Seeded seeded = await SeedAsync(store, clock);
        var attention = new AttentionService(store);

        Assert.Equal([seeded.Early, seeded.Late], await ListedAsync(attention, Owner, Alpha));
        Assert.Equal([seeded.Middle], await ListedAsync(attention, Owner, $" {Beta} "));

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => attention.ListAsync(Owner, "gamma", None));
        Assert.Equal("Project 'gamma' is not registered; register it with coa project add gamma [--repo <url>].", refused.Message);
    }

    [Fact]
    public async Task A_paused_flow_run_another_node_started_is_not_listed_beside_one_this_node_started_in_the_same_project()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        var clock = new Clock();
        Seeded seeded = await SeedAsync(store, clock);
        var parcels = new ParcelService(store, clock);
        var flowRuns = new FlowRunService(store, clock);

        Parcel imported = await parcels.AddAsync(Owner, Alpha, "task", "A peer's", null, None);
        FlowRun peers = await flowRuns.StartAsync(Owner, Alpha, imported.Id, "build", None);
        clock.Now = Start.AddMinutes(5);
        await flowRuns.PauseAsync(Owner, Alpha, peers.Id, "A peer's pause", "Resume it on the peer", None);
        await RecordPeerLinesAsync(store, Alpha, Ids.New(), peers.Id);

        var attention = new AttentionService(store);
        Assert.Equal([seeded.Early, seeded.Late], await ListedAsync(attention, Owner, Alpha));
        Assert.DoesNotContain(peers.Id, await ListedAsync(attention, Owner, null));
    }

    [Fact]
    public async Task The_time_given_is_the_latest_pause_unmoved_by_usage_recorded_after_it()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        var clock = new Clock();
        await RegisteredProjects.AddAsync(store, Alpha, None);
        var parcels = new ParcelService(store, clock);
        var flowRuns = new FlowRunService(store, clock);
        var attention = new AttentionService(store);

        Parcel parcel = await parcels.AddAsync(Owner, Alpha, "task", "Paused twice", null, None);
        FlowRun flowRun = await LaunchedBuildAsync(flowRuns, Alpha, parcel.Id);
        clock.Now = Start.AddMinutes(1);
        await flowRuns.PauseAsync(Owner, Alpha, flowRun.Id, "First pause", "Resume it", None);

        // An agent step's usage recorded after the pause moves the document's update time and not the pause's.
        clock.Now = Start.AddMinutes(2);
        FlowRun used = await flowRuns.RecordUsageAsync(Owner, Alpha, flowRun.Id, "build", "build", Ids.New(), Scripts.Usage(1), None);
        Assert.Equal(Start.AddMinutes(2), used.UpdatedAt);
        PausedFlowRun first = Assert.Single(await attention.ListAsync(Owner, null, None));
        Assert.Equal(Start.AddMinutes(1), first.PausedAt);

        // Resumed and paused again, the entry gives the second pause's time, reason, and lever.
        clock.Now = Start.AddMinutes(3);
        await flowRuns.ResumeAsync(Owner, Alpha, flowRun.Id, null, null, None);
        Assert.Empty(await attention.ListAsync(Owner, null, None));
        clock.Now = Start.AddMinutes(4);
        await flowRuns.PauseAsync(Owner, Alpha, flowRun.Id, "Second pause", "Resume it again", None);
        clock.Now = Start.AddMinutes(5);
        await flowRuns.RecordUsageAsync(Owner, Alpha, flowRun.Id, "build", "build", Ids.New(), Scripts.Usage(1), None);

        PausedFlowRun second = Assert.Single(await attention.ListAsync(Owner, Alpha, None));
        Assert.Equal(Start.AddMinutes(4), second.PausedAt);
        Assert.Equal("Second pause", second.Reason);
        Assert.Equal("Resume it again", second.Lever);
    }

    [Fact]
    public async Task A_flow_run_paused_before_this_query_existed_gives_its_pause_time_from_its_stream()
    {
        // The flow run's document holds no pause time, so a document stored by a build before this query has
        // the shape one stored now has; the time is read from the latest pause event in the stream. This
        // writes the start and the pause as raw events, as any earlier build recorded them, and builds the
        // document from them alone.
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        var clock = new Clock();
        await RegisteredProjects.AddAsync(store, Alpha, None);
        var parcels = new ParcelService(store, clock);
        var flowRuns = new FlowRunService(store, clock);
        Parcel parcel = await parcels.AddAsync(Owner, Alpha, "task", "Paused long ago", null, None);
        FlowRun flowRun = await flowRuns.StartAsync(Owner, Alpha, parcel.Id, "build", None);
        await using (IDocumentSession session = store.LightweightSession(Alpha))
        {
            session.Events.Append(flowRun.Id, new Coanda.Core.FlowRuns.Events.FlowRunPaused(flowRun.Id, "Paused long ago", "Resume it", Owner.Id, Start.AddDays(-30)));
            await session.SaveChangesAsync(None);
        }

        PausedFlowRun paused = Assert.Single(await new AttentionService(store).ListAsync(Owner, null, None));
        Assert.Equal(flowRun.Id, paused.FlowRunId);
        Assert.Equal(Start.AddDays(-30), paused.PausedAt);
    }

    [Fact]
    public async Task A_module_is_answered_only_for_projects_its_module_is_switched_on_for()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        var clock = new Clock();
        Seeded seeded = await SeedAsync(store, clock);
        await ProjectServices.Software(store, clock).SetModulesAsync(Owner, Beta, [], None);
        var attention = new AttentionService(store);

        Assert.Equal([seeded.Early, seeded.Late], await ListedAsync(attention, Software, null));
        Assert.Equal([seeded.Early, seeded.Late], await ListedAsync(attention, Software, Alpha));

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => attention.ListAsync(Software, Beta, None));
        Assert.Equal($"Module '{SoftwareSteps.Module}' is not switched on for project '{Beta}'; a module acts only in a project it is switched on for.", refused.Message);

        // The owner still reads both.
        Assert.Equal([seeded.Early, seeded.Middle, seeded.Late], await ListedAsync(attention, Owner, null));
    }

    [Fact]
    public async Task A_session_is_answered_only_for_its_bound_project_and_an_agent_with_no_session_for_every_project()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        var clock = new Clock();
        Seeded seeded = await SeedAsync(store, clock);
        var attention = new AttentionService(store);
        var binding = new SessionBinding(Beta, seeded.Middle, Ids.New());
        Principal session = Principal.ForSession(binding);

        Assert.Equal([seeded.Middle], await ListedAsync(attention, session, null));
        Assert.Equal([seeded.Middle], await ListedAsync(attention, session, Beta));

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => attention.ListAsync(session, Alpha, None));
        Assert.Contains($"project '{Alpha}' is not project '{Beta}'", refused.Message, StringComparison.Ordinal);
        Assert.StartsWith(SessionScope.BoundRule(binding), refused.Message, StringComparison.Ordinal);

        Assert.Equal([seeded.Early, seeded.Middle, seeded.Late], await ListedAsync(attention, Loop, null));
        Assert.Equal([seeded.Early, seeded.Late], await ListedAsync(attention, Loop, Alpha));
    }

    [Fact]
    public async Task Nothing_paused_lists_nothing_and_a_blank_project_is_refused()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Alpha, None);
        var attention = new AttentionService(store);

        Assert.Empty(await attention.ListAsync(Owner, null, None));
        Assert.Empty(await attention.ListAsync(Owner, Alpha, None));
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => attention.ListAsync(Owner, " ", None));
        Assert.Equal("A project name is required; none was given.", refused.Message);
    }
}
