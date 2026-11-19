using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// A parcel entering a stage its type binds a flow to while parcels blocking it are open waits, and the bound flow
/// starts once the last of them closes, on the node whose claims let it start the flow. Each case moves a task into
/// build, which binds the build flow, after recording through the parcel service that other tasks block it.
/// </summary>
public sealed class BlockedEntryTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    [Fact]
    public async Task An_entry_blocked_by_two_parcels_starts_nothing_when_the_first_closes_and_starts_the_flow_when_the_second_does()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();

        Guid first = await TaskAsync(parcels, "First blocker");
        Guid second = await TaskAsync(parcels, "Second blocker");
        Guid blocked = await TaskAsync(parcels, "Blocked by both");
        await BlockAsync(parcels, blocked, first, second);
        Guid entry = await MoveAsync(parcels, store, blocked, "build");
        await UntilWaitingAsync(store, blocked);
        int length = (await EventsOfAsync(store, blocked)).Count;

        await parcels.CloseAsync(Owner, Project, first, "completed", "Done first", None);
        await HandledAsync(client);
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, blocked));
        // the wait recorded no event on the parcel
        Assert.Equal(length, (await EventsOfAsync(store, blocked)).Count);
        Assert.NotNull(await WaitingAsync(store, blocked));

        await parcels.CloseAsync(Owner, Project, second, "abandoned", "Dropped", None);
        FlowRunView bound = await FlowRunDriver.BoundAsync(client, Project, blocked);
        Assert.Equal("build", bound.FlowName);
        Assert.Equal($"binding:task/build/build:entry:{entry}", bound.StartedBy);
        await UntilNotWaitingAsync(store, blocked);

        // the close handled again starts no second flow run
        await HandledAsync(client);
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, blocked));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_entry_whose_blockers_are_closed_and_one_blocked_only_after_it_start_the_flow_at_the_entry()
    {
        using var temp = new TemporaryHome();
        Guid closedFirst = Guid.Empty;
        Guid blockedLater = Guid.Empty;
        await SeedAsync(temp, async (store, parcels, flowRuns) =>
        {
            Guid closed = await TaskAsync(parcels, "Closed before the entry");
            await parcels.CloseAsync(Owner, Project, closed, "completed", "Done", None);
            closedFirst = await TaskAsync(parcels, "Blocked by a closed parcel");
            await BlockAsync(parcels, closedFirst, closed);
            await parcels.MoveAsync(Owner, Project, closedFirst, "build", None);

            Guid open = await TaskAsync(parcels, "Still open");
            blockedLater = await TaskAsync(parcels, "Blocked after its entry");
            await parcels.MoveAsync(Owner, Project, blockedLater, "build", None);
            await BlockAsync(parcels, blockedLater, open);
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, closedFirst)).FlowName);
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, blockedLater)).FlowName);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_entry_made_while_the_parcel_has_a_live_flow_run_is_refused_whatever_its_blockers_and_never_waits()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();

        Guid blocker = await TaskAsync(parcels, "Open blocker");
        Guid held = await TaskAsync(parcels, "Moved under a live flow run");
        await BlockAsync(parcels, held, blocker);
        FlowRun holding = await flowRuns.StartAsync(Owner, Project, held, "build", None);
        await parcels.MoveAsync(FlowRunAdvancer.PrincipalFor(holding.Id), Project, held, "build", holding.Id, None);
        await HandledAsync(client);

        Assert.Null(await WaitingAsync(store, held));
        await flowRuns.StopAsync(Owner, Project, holding.Id, "Out of the way", None);
        await parcels.CloseAsync(Owner, Project, blocker, "completed", "Done", None);
        await HandledAsync(client);
        Assert.Equal([holding.Id], (await FlowRunDriver.OfParcelAsync(client, Project, held)).Select(f => f.Id));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_wait_that_ends_on_a_closed_moved_or_busy_parcel_starts_nothing_and_no_longer_waits()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();

        Guid blocker = await TaskAsync(parcels, "Blocks three");
        Guid closed = await TaskAsync(parcels, "Closed while it waits");
        Guid moved = await TaskAsync(parcels, "Moved while it waits");
        Guid busy = await TaskAsync(parcels, "Busy while it waits");
        foreach (Guid parcel in new[] { closed, moved, busy })
        {
            await BlockAsync(parcels, parcel, blocker);
            await MoveAsync(parcels, store, parcel, "build");
            await UntilWaitingAsync(store, parcel);
        }

        await parcels.CloseAsync(Owner, Project, closed, "abandoned", "Not needed", None);
        await parcels.MoveAsync(Owner, Project, moved, "review", None);
        Guid back = await MoveAsync(parcels, store, moved, "build");
        await FlowRunDriver.UntilAsync(async () => (await WaitingAsync(store, moved))?.Entries.Count == 2, "the second entry waits");
        FlowRun live = await flowRuns.StartAsync(Owner, Project, busy, "build", None);

        await parcels.CloseAsync(Owner, Project, blocker, "completed", "Done", None);
        // only the entry the parcel came back with starts the flow
        FlowRunView bound = await FlowRunDriver.BoundAsync(client, Project, moved);
        Assert.Equal($"binding:task/build/build:entry:{back}", bound.StartedBy);
        await HandledAsync(client);

        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, closed));
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, moved));
        Assert.Equal([live.Id], (await FlowRunDriver.OfParcelAsync(client, Project, busy)).Select(f => f.Id));
        foreach (Guid parcel in new[] { closed, moved, busy })
        {
            Assert.Null(await WaitingAsync(store, parcel));
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task One_close_whose_start_fails_on_one_blocked_parcel_still_starts_the_flow_on_the_other()
    {
        using var temp = new TemporaryHome();
        Guid failing = Guid.Empty;
        int failed = 0;
        Task Fail(WaitedEntryStart start, CancellationToken token)
        {
            if (start.ParcelId == failing)
            {
                Interlocked.Increment(ref failed);
                throw new InvalidOperationException("A start that fails for a reason other than a refusal.");
            }

            return Task.CompletedTask;
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { WaitedEntryStarting = Fail }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();

        Guid blocker = await TaskAsync(parcels, "Blocks two");
        failing = await TaskAsync(parcels, "Fails to start");
        Guid starting = await TaskAsync(parcels, "Starts");
        foreach (Guid parcel in new[] { failing, starting })
        {
            await BlockAsync(parcels, parcel, blocker);
            await MoveAsync(parcels, store, parcel, "build");
            await UntilWaitingAsync(store, parcel);
        }

        await parcels.CloseAsync(Owner, Project, blocker, "completed", "Done", None);
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, starting)).FlowName);
        await HandledAsync(client);

        Assert.Equal(1, failed);
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, failing));
        Assert.Null(await WaitingAsync(store, failing));
        Assert.Null(await WaitingAsync(store, starting));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_stop_while_a_waited_start_is_held_open_keeps_the_entry_waiting_and_the_next_start_starts_it_once()
    {
        using var temp = new TemporaryHome();
        Guid blocked;
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Hold(WaitedEntryStart start, CancellationToken token)
        {
            holding.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { WaitedEntryStarting = Hold }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            IDocumentStore store = first.Services.GetRequiredService<IDocumentStore>();
            ParcelService parcels = first.Services.GetRequiredService<ParcelService>();
            Guid blocker = await TaskAsync(parcels, "Closes before the stop");
            blocked = await TaskAsync(parcels, "Starts after the stop");
            await BlockAsync(parcels, blocked, blocker);
            await MoveAsync(parcels, store, blocked, "build");
            await UntilWaitingAsync(store, blocked);
            await parcels.CloseAsync(Owner, Project, blocker, "completed", "Done", None);
            await holding.Task.WaitAsync(TestPatience.Bound);
            await first.StopAsync(None);
        }

        await using (IDocumentStore stopped = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            Assert.NotNull(await WaitingAsync(stopped, blocked));
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, blocked)).FlowName);
        await HandledAsync(client);
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, blocked));
        await second.StopAsync(None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_entry_waiting_at_a_stop_starts_its_flow_when_its_blocker_closed_while_stopped_and_after_an_export_and_rebuild(bool rebuild)
    {
        using var temp = new TemporaryHome();
        Guid blocker;
        Guid blocked;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            IDocumentStore store = first.Services.GetRequiredService<IDocumentStore>();
            ParcelService parcels = first.Services.GetRequiredService<ParcelService>();
            blocker = await TaskAsync(parcels, "Closes while stopped");
            blocked = await TaskAsync(parcels, "Waits across the stop");
            await BlockAsync(parcels, blocked, blocker);
            await MoveAsync(parcels, store, blocked, "build");
            await UntilWaitingAsync(store, blocked);
            await first.StopAsync(None);
        }

        await using (IDocumentStore stopped = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await new ParcelService(stopped, TimeProvider.System).CloseAsync(Owner, Project, blocker, "completed", "Done while stopped", None);
        }

        if (rebuild)
        {
            RecordExportResult export = await RecordExport.WriteAsync(temp.Home, TimeProvider.System, None);
            await RecordRebuild.RunAsync(temp.Home, export.Path, TimeProvider.System, None);
            await using IDocumentStore rebuilt = StoreConfiguration.Open(temp.Home.DatabasePath);
            Assert.NotNull(await WaitingAsync(rebuilt, blocked));
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, blocked)).FlowName);
        await UntilNotWaitingAsync(second.Services.GetRequiredService<IDocumentStore>(), blocked);
        await second.StopAsync(None);
    }

    [Fact]
    public async Task On_a_parcel_this_node_holds_a_blockers_close_imported_from_a_peer_starts_the_flow()
    {
        using var temp = new TemporaryHome();
        NodeIdentity identity = await NodeIdentity.EnsureAsync(temp.Home, None);
        Guid blocker = Guid.Empty;
        Guid blocked = Guid.Empty;
        await SeedAsync(temp, async (store, parcels, flowRuns) =>
        {
            blocker = await TaskAsync(parcels, "Closed by a peer");
            blocked = await TaskAsync(parcels, "Held here");
            await BlockAsync(parcels, blocked, blocker);
            await HoldAsync(store, parcels, identity, blocked);
            await parcels.MoveAsync(Owner, Project, blocked, "build", None);
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        await UntilWaitingAsync(store, blocked);

        await AppendAsPeerAsync(store, Ids.New(), blocker, Closed(blocker));
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, blocked)).FlowName);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task On_a_parcel_whose_claim_this_node_lost_while_it_waited_the_blockers_close_starts_nothing()
    {
        using var temp = new TemporaryHome();
        NodeIdentity identity = await NodeIdentity.EnsureAsync(temp.Home, None);
        Guid blocker = Guid.Empty;
        Guid blocked = Guid.Empty;
        await SeedAsync(temp, async (store, parcels, flowRuns) =>
        {
            blocker = await TaskAsync(parcels, "Closes after the claim is lost");
            blocked = await TaskAsync(parcels, "Lost to a peer");
            await BlockAsync(parcels, blocked, blocker);
            await HoldAsync(store, parcels, identity, blocked);
            await parcels.MoveAsync(Owner, Project, blocked, "build", None);
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
        await UntilWaitingAsync(store, blocked);

        // The peer's claim, on a line whose hash is the lowest there is, wins the parcel.
        Guid peer = Ids.New();
        await AppendAsPeerAsync(store, peer, blocked, $"sha256:{new string('0', 64)}", new ParcelClaimed(blocked, peer, "peer", DateTimeOffset.UtcNow));
        await parcels.CloseAsync(Owner, Project, blocker, "completed", "Done", None);
        await UntilNotWaitingAsync(store, blocked);
        await HandledAsync(client);
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, blocked));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_last_blocker_closing_while_this_nodes_claim_is_not_yet_held_starts_the_flow_once_the_claim_is_held()
    {
        using var temp = new TemporaryHome();
        NodeIdentity identity = await NodeIdentity.EnsureAsync(temp.Home, None);
        Guid blocker;
        Guid blocked;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            // Unclaimed, so this node's own entry is judged and waits.
            await RegisteredProjects.AddAsync(first, Project, None);
            IDocumentStore store = first.Services.GetRequiredService<IDocumentStore>();
            ParcelService parcels = first.Services.GetRequiredService<ParcelService>();
            blocker = await TaskAsync(parcels, "Closes before the claim is held");
            blocked = await TaskAsync(parcels, "Claimed while it waits");
            await BlockAsync(parcels, blocked, blocker);
            await MoveAsync(parcels, store, blocked, "build");
            await UntilWaitingAsync(store, blocked);
            await first.StopAsync(None);
        }

        long through;
        await using (IDocumentStore stopped = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            // This node's claim, on a line of its chain and pushed, with no fetch since; then the blocker closes.
            var parcels = new ParcelService(stopped, TimeProvider.System);
            await parcels.ClaimAsync(Owner, Project, blocked, identity.NodeId, None);
            through = await LineAsync(stopped, identity, blocked);
            await LedgerProgress.AdvanceAsync(stopped, Project, LedgerProgress.PushedId, through, None);
            await parcels.CloseAsync(Owner, Project, blocker, "completed", "Done", None);
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore running = second.Services.GetRequiredService<IDocumentStore>();
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                await using IQuerySession session = running.QuerySession(Project);
                return await session.LoadAsync<HeldBackEntries>(blocked, None) is not null;
            },
            "the entry is held back");
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, blocked));

        await LedgerProgress.AdvanceAsync(running, Project, LedgerProgress.FetchedId, through, None);
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, blocked)).FlowName);
        await UntilNotWaitingAsync(running, blocked);
        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_blocker_this_node_has_not_imported_counts_as_open_until_it_is_imported_closed_or_closes_later()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
        Guid peer = Ids.New();

        // Two parcels a peer recorded as blocked by parcels of its own that this node has not imported.
        Guid importedClosed = Ids.New();
        Guid closesLater = Ids.New();
        Guid first = await TaskAsync(parcels, "Waits on a blocker imported closed");
        Guid second = await TaskAsync(parcels, "Waits on a blocker that closes later");
        await AppendAsPeerAsync(store, peer, first, new ParcelRelationRecorded(first, RelationKinds.BlockedBy, importedClosed, null, "peer", DateTimeOffset.UtcNow));
        await AppendAsPeerAsync(store, peer, second, new ParcelRelationRecorded(second, RelationKinds.BlockedBy, closesLater, null, "peer", DateTimeOffset.UtcNow));
        foreach (Guid parcel in new[] { first, second })
        {
            await MoveAsync(parcels, store, parcel, "build");
            await UntilWaitingAsync(store, parcel);
        }

        Parcel shape = await parcels.ShowAsync(Owner, Project, first, None) ?? throw new InvalidOperationException("No parcel.");
        await AppendAsPeerAsync(store, peer, importedClosed, Created(importedClosed, shape.TypeHash), Closed(importedClosed));
        await AppendAsPeerAsync(store, peer, closesLater, Created(closesLater, shape.TypeHash));
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, first)).FlowName);
        await HandledAsync(client);
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, second));
        Assert.NotNull(await WaitingAsync(store, second));

        await AppendAsPeerAsync(store, peer, closesLater, Closed(closesLater));
        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, second)).FlowName);
        await daemon.StopAsync(None);
    }

    /// <summary>Starts and stops a daemon on the home, so its trigger position is fixed, then registers the project and writes what <paramref name="seed"/> does.</summary>
    private static async Task SeedAsync(TemporaryHome temp, Func<IDocumentStore, ParcelService, FlowRunService, Task> seed)
    {
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            await first.StopAsync(None);
        }

        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        await seed(store, new ParcelService(store, TimeProvider.System), new FlowRunService(store, TimeProvider.System));
    }

    /// <summary>A task in refine with the fields it needs to leave refine and build.</summary>
    private static async Task<Guid> TaskAsync(ParcelService parcels, string title)
    {
        Parcel parcel = await parcels.AddAsync(Owner, Project, "task", title, null, None);
        await parcels.BriefAsync(Owner, Project, parcel.Id, cancellationToken: None);
        return parcel.Id;
    }

    private static async Task BlockAsync(ParcelService parcels, Guid parcel, params Guid[] blockers)
    {
        foreach (Guid blocker in blockers)
        {
            await parcels.RelateAsync(Owner, Project, parcel, RelationKinds.BlockedBy, blocker, null, None);
        }
    }

    /// <summary>Moves the parcel and returns the id of its move event, which a binding's start for that entry is named for.</summary>
    private static async Task<Guid> MoveAsync(ParcelService parcels, IDocumentStore store, Guid parcel, string to)
    {
        await parcels.MoveAsync(Owner, Project, parcel, to, None);
        return (await EventsOfAsync(store, parcel))[^1].Id;
    }

    /// <summary>
    /// Waits until the loop has handled every entry recorded so far: it handles entries in order, so once a task moved
    /// into build after them has its flow run, each of them was handled.
    /// </summary>
    private static async Task HandledAsync(DaemonClient client) =>
        await FlowRunDriver.BoundAsync(client, Project, (await FlowRunDriver.TaskAtBuildAsync(client, Project, "Marks the record handled")).Id);

    private static async Task<IReadOnlyList<IEvent>> EventsOfAsync(IDocumentStore store, Guid stream)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return await session.Events.FetchStreamAsync(stream, token: None);
    }

    private static async Task<WaitingEntries?> WaitingAsync(IDocumentStore store, Guid parcel)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return await session.LoadAsync<WaitingEntries>(parcel, None);
    }

    private static Task UntilWaitingAsync(IDocumentStore store, Guid parcel) =>
        FlowRunDriver.UntilAsync(async () => await WaitingAsync(store, parcel) is not null, "the entry waits");

    private static Task UntilNotWaitingAsync(IDocumentStore store, Guid parcel) =>
        FlowRunDriver.UntilAsync(async () => await WaitingAsync(store, parcel) is null, "the entry no longer waits");

    /// <summary>Claims the parcel for this node, on a line of its chain, pushed and followed by a fetch that succeeded.</summary>
    private static async Task HoldAsync(IDocumentStore store, ParcelService parcels, NodeIdentity identity, Guid parcel)
    {
        await parcels.ClaimAsync(Owner, Project, parcel, identity.NodeId, None);
        long through = await LineAsync(store, identity, parcel);
        await LedgerProgress.AdvanceAsync(store, Project, LedgerProgress.PushedId, through, None);
        await LedgerProgress.AdvanceAsync(store, Project, LedgerProgress.FetchedId, through, None);
    }

    /// <summary>Gives every event of the parcel's stream a line of this node's chain, numbered from 1, and returns the last line's sequence.</summary>
    private static async Task<long> LineAsync(IDocumentStore store, NodeIdentity identity, Guid parcel)
    {
        await using (IDocumentSession node = store.LightweightSession(Tenants.Node))
        {
            node.Store(new LedgerOrigin { NodeId = identity.NodeId });
            await node.SaveChangesAsync(None);
        }

        await using IDocumentSession session = store.LightweightSession(Project);
        long sequence = 0;
        foreach (IEvent entry in await session.Events.FetchStreamAsync(parcel, token: None))
        {
            session.Store(new LedgerLine { Id = entry.Id, Origin = identity.NodeId, Sequence = ++sequence, Hash = RandomHash() });
        }

        await session.SaveChangesAsync(None);
        return sequence;
    }

    private static Task AppendAsPeerAsync(IDocumentStore store, Guid origin, Guid stream, params object[] data) =>
        AppendAsPeerAsync(store, origin, stream, null, data);

    /// <summary>Appends <paramref name="data"/> to <paramref name="stream"/> with the record the import keeps for lines of <paramref name="origin"/>, in one save.</summary>
    private static async Task AppendAsPeerAsync(IDocumentStore store, Guid origin, Guid stream, string? hash, params object[] data)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        IReadOnlyList<IEvent> appended = session.Events.Append(stream, data).Events;
        foreach (IEvent entry in appended)
        {
            session.Store(new LedgerLine { Id = entry.Id, Origin = origin, Sequence = 1, Hash = hash ?? RandomHash() });
        }

        await session.SaveChangesAsync(None);
    }

    private static string RandomHash() => $"sha256:{Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())}{new string('f', 32)}";

    private static ParcelCreated Created(Guid parcel, string typeHash) =>
        new(parcel, Project, "task", typeHash, "refine", "A peer's blocker", "peer", DateTimeOffset.UtcNow);

    private static ParcelClosed Closed(Guid parcel) =>
        new(parcel, "refine", "closed", "completed", "Done on a peer", "peer", DateTimeOffset.UtcNow);
}
