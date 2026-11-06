using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// An event the import appended from another node's lines is that node's work: the trigger loop starts
/// nothing on it unless this node holds its parcel, the advance at the daemon's start leaves alone a live flow
/// run whose start was imported, the flow run service writes nothing to such a flow run, and the module feed
/// delivers no end or stop of it. Each case stands an imported event in by giving it the record the import
/// saves with it, a <see cref="LedgerLine"/> naming another origin, and sits beside a local case the daemon does act on.
/// </summary>
public sealed class ImportedEventsTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_peers_bound_entry_starts_nothing_and_a_peers_live_flow_run_is_not_advanced_at_start()
    {
        using var temp = new TemporaryHome();
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            await first.StopAsync(None);
        }

        Guid peer = Ids.New();
        Guid enteredByPeer;
        Guid startedByPeer;
        Guid startedHere;
        int peerEvents;
        int localEvents;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            var parcels = new ParcelService(store, TimeProvider.System);
            var flowRuns = new FlowRunService(store, TimeProvider.System);

            // A peer's parcel entering a bound stage, which a local entry like it starts a flow run on.
            enteredByPeer = await AtBuildAsync(parcels, "Entered by a peer");
            await RecordLinesAsync(store, peer, enteredByPeer);

            // A peer's flow run, live on its parcel, and a local one beside it.
            Guid peerParcel = await AtBuildAsync(parcels, "Building on a peer");
            startedByPeer = (await flowRuns.StartAsync(Principal.LocalOwner, Project, peerParcel, "build", None)).Id;
            await RecordLinesAsync(store, peer, peerParcel, startedByPeer);
            Guid localParcel = await AtBuildAsync(parcels, "Building here");
            startedHere = (await flowRuns.StartAsync(Principal.LocalOwner, Project, localParcel, "build", None)).Id;
            peerEvents = await StreamLengthAsync(store, startedByPeer);
            localEvents = await StreamLengthAsync(store, startedHere);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore running = daemon.Services.GetRequiredService<IDocumentStore>();

        // The loop handles entries in order, so once a later entry has its flow run, the peer's entry was read.
        ParcelView later = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Moved after the import");
        await FlowRunDriver.BoundAsync(client, Project, later.Id);
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, enteredByPeer));

        // The start advanced the local flow run, and the peer's was asked for in the same pass.
        await FlowRunDriver.UntilAsync(async () => await StreamLengthAsync(running, startedHere) > localEvents, "the local flow run advanced");
        Assert.Equal(peerEvents, await StreamLengthAsync(running, startedByPeer));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_node_whose_trigger_position_is_fixed_starts_nothing_from_an_imported_history_on_unclaimed_or_peer_claimed_parcels()
    {
        using var temp = new TemporaryHome();
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            await first.StopAsync(None);
        }

        Guid peer = Ids.New();
        Guid[] entered;
        Guid[] ended;
        string origin;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            var parcels = new ParcelService(store, TimeProvider.System);
            var flowRuns = new FlowRunService(store, TimeProvider.System);
            var triggers = new TriggerService(store, TimeProvider.System);

            // Bound stage entries and flow run ends the peer recorded, on a parcel left unclaimed and on one it claimed.
            entered = [await AtBuildAsync(parcels, "Entered, unclaimed"), await AtBuildAsync(parcels, "Entered, claimed")];
            ended = [await AtBuildAsync(parcels, "Ended, unclaimed"), await AtBuildAsync(parcels, "Ended, claimed")];
            List<Guid> streams = [.. entered, .. ended];
            foreach (Guid parcel in ended)
            {
                FlowRun run = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel, SoftwareFlows.Build.Name, None);
                await flowRuns.EndAsync(Principal.LocalOwner, Project, run.Id, SoftwareFlows.Build.Outcome, None);
                streams.Add(run.Id);
            }

            await ClaimAsync(store, peer, entered[1], ended[1]);

            // An external event the peer recorded, which a subscription here matches.
            await triggers.SubscribeAsync(
                Principal.LocalOwner, Project, new Trigger("pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian" }),
                "task", "title", "review-on-assignment", None);
            ExternalEvent external = await triggers.RecordEventAsync(
                Principal.LocalOwner, Project, "pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian", ["title"] = "A peer's review" }, None);
            streams.Add(external.Id);
            origin = (await triggers.ListSubscriptionsAsync(Principal.LocalOwner, Project, None)).Single().OriginFor(external.Id);
            await RecordLinesAsync(store, peer, [.. streams]);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        // The loop handles entries in order, so once a later entry has its flow run, the imported history was read.
        ParcelView later = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Moved after the import");
        await FlowRunDriver.BoundAsync(client, Project, later.Id);
        foreach (Guid parcel in entered)
        {
            Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, parcel));
        }

        foreach (Guid parcel in ended)
        {
            Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, parcel));
        }

        Assert.Null(await daemon.Services.GetRequiredService<ParcelService>().FindByOriginAsync(Principal.LocalOwner, Project, origin, None));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task On_a_parcel_this_node_holds_a_peers_bound_entry_starts_the_bound_flow()
    {
        using var temp = new TemporaryHome();
        NodeIdentity identity = await NodeIdentity.EnsureAsync(temp.Home, None);
        Guid held = Guid.Empty;
        await SeedAsync(temp, async (store, parcels, flowRuns) =>
        {
            Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Held here, moved by a peer", null, None);
            await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
            held = parcel.Id;

            // This node's claim, on the first line of its chain, pushed and followed by a fetch that succeeded.
            await parcels.ClaimAsync(Principal.LocalOwner, Project, parcel.Id, identity.NodeId, None);
            await using (IDocumentSession node = store.LightweightSession(Tenants.Node))
            {
                node.Store(new LedgerOrigin { NodeId = identity.NodeId });
                await node.SaveChangesAsync(None);
            }

            await RecordLinesAsync(store, identity.NodeId, parcel.Id);
            long through = (await EventsOfAsync(store, parcel.Id)).Count;
            await LedgerProgress.AdvanceAsync(store, Project, LedgerProgress.PushedId, through, None);
            await LedgerProgress.AdvanceAsync(store, Project, LedgerProgress.FetchedId, through, None);

            // The peer's move into the bound stage.
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            IEvent moved = (await EventsOfAsync(store, parcel.Id))[^1];
            await using IDocumentSession session = store.LightweightSession(Project);
            session.Store(new LedgerLine { Id = moved.Id, Origin = Ids.New(), Sequence = 1, Hash = $"sha256:{new string('f', 64)}" });
            await session.SaveChangesAsync(None);
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        Assert.Equal(SoftwareFlows.Build.Name, (await FlowRunDriver.BoundAsync(client, Project, held)).FlowName);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_neither_advances_nor_spawns_an_agent_step_for_a_peers_live_flow_run()
    {
        using var temp = new TemporaryHome();
        Guid peers = Guid.Empty;
        int events = 0;
        await SeedAsync(temp, async (store, parcels, flowRuns) =>
        {
            Guid parcel = await AtBuildAsync(parcels, "Building on a peer");
            peers = (await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel, SoftwareFlows.Build.Name, None)).Id;
            await RecordLinesAsync(store, Ids.New(), parcel, peers);
            events = await StreamLengthAsync(store, peers);
        });

        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>());
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);

        // The start's advance is asked for before the daemon answers, so a quiet spell after it is long enough.
        await Task.Delay(TimeSpan.FromMilliseconds(500), None);
        Assert.Equal(events, await StreamLengthAsync(daemon.Services.GetRequiredService<IDocumentStore>(), peers));
        Assert.Empty(executor.Spawns);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_restart_adopts_this_nodes_own_live_flow_run_once_its_start_is_exported()
    {
        using var temp = new TemporaryHome();
        NodeIdentity identity = await NodeIdentity.EnsureAsync(temp.Home, None);
        Guid exported = Guid.Empty;
        int events = 0;
        await SeedAsync(temp, async (store, parcels, flowRuns) =>
        {
            Guid parcel = await AtBuildAsync(parcels, "Building here, exported");
            exported = (await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel, SoftwareFlows.Build.Name, None)).Id;

            // The export records the id it writes this node's lines under before its first line, then a record per line.
            await using (IDocumentSession node = store.LightweightSession(Tenants.Node))
            {
                node.Store(new LedgerOrigin { NodeId = identity.NodeId });
                await node.SaveChangesAsync(None);
            }

            await RecordLinesAsync(store, identity.NodeId, parcel, exported);
            events = await StreamLengthAsync(store, exported);
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        IDocumentStore running = daemon.Services.GetRequiredService<IDocumentStore>();
        await FlowRunDriver.UntilAsync(async () => await StreamLengthAsync(running, exported) > events, "this node's exported flow run advanced");
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

    [Fact]
    public async Task Every_write_to_a_flow_run_another_node_started_is_refused_naming_that_node_whoever_asks()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.EnsureDatabase().DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, None);
        var parcels = new ParcelService(store, TimeProvider.System);
        var flowRuns = new FlowRunService(store, TimeProvider.System);
        Guid peer = Ids.New();
        Guid parcel = await AtBuildAsync(parcels, "A peer's");
        FlowRun run = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel, SoftwareFlows.Build.Name, None);
        await RecordLinesAsync(store, peer, parcel, run.Id);
        int events = await StreamLengthAsync(store, run.Id);
        Principal runner = FlowRunAdvancer.PrincipalFor(run.Id);

        Func<Task>[] writes =
        [
            () => flowRuns.ResumeAsync(Principal.LocalOwner, Project, run.Id, null, None),
            () => flowRuns.PauseAsync(Principal.LocalOwner, Project, run.Id, "Look first.", "resume", None),
            () => flowRuns.PauseAsync(runner, Project, run.Id, "Look first.", "resume", None),
            () => flowRuns.StopAsync(Principal.LocalOwner, Project, run.Id, "Not ours.", None),
            () => flowRuns.EndAsync(Principal.LocalOwner, Project, run.Id, SoftwareFlows.Build.Outcome, None),
            () => flowRuns.EndAsync(runner, Project, run.Id, SoftwareFlows.Build.Outcome, None),
            () => flowRuns.StartStepAsync(runner, Project, run.Id, run.CurrentStep, None),
            () => flowRuns.DispatchedAsync(runner, Project, run.Id, run.CurrentStep, None),
        ];
        foreach (Func<Task> write in writes)
        {
            DomainException refused = await Assert.ThrowsAsync<DomainException>(write);
            Assert.Contains($"was started by node {peer}", refused.Message, StringComparison.Ordinal);
        }

        Assert.Equal(events, await StreamLengthAsync(store, run.Id));
    }

    /// <summary>Records a claim by <paramref name="peer"/> on each of <paramref name="parcels"/>, on their streams, to be marked as the peer's.</summary>
    private static async Task ClaimAsync(IDocumentStore store, Guid peer, params Guid[] parcels)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        foreach (Guid parcel in parcels)
        {
            session.Events.Append(parcel, new ParcelClaimed(parcel, peer, "peer", DateTimeOffset.UtcNow));
        }

        await session.SaveChangesAsync(None);
    }

    private static async Task<Guid> AtBuildAsync(ParcelService parcels, string title)
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", title, null, None);
        await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
        await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
        return parcel.Id;
    }

    /// <summary>
    /// Gives every event of <paramref name="streams"/> the record kept for a line of <paramref name="origin"/>: the
    /// import's for another node's line, or the export's when <paramref name="origin"/> is this node.
    /// </summary>
    private static async Task RecordLinesAsync(IDocumentStore store, Guid origin, params Guid[] streams)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        long sequence = 0;
        foreach (Guid stream in streams)
        {
            foreach (IEvent entry in await session.Events.FetchStreamAsync(stream, token: None))
            {
                session.Store(new LedgerLine { Id = entry.Id, Origin = origin, Sequence = ++sequence, Hash = $"sha256:{Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())}{new string('0', 32)}" });
            }
        }

        await session.SaveChangesAsync(None);
    }

    private static async Task<IReadOnlyList<IEvent>> EventsOfAsync(IDocumentStore store, Guid stream)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return await session.Events.FetchStreamAsync(stream, token: None);
    }

    private static async Task<int> StreamLengthAsync(IDocumentStore store, Guid stream) => (await EventsOfAsync(store, stream)).Count;
}
