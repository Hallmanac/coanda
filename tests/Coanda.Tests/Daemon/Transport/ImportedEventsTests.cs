using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// An event the import appended from another node's lines is that node's work: the trigger loop starts
/// nothing on it, and the advance at the daemon's start leaves alone a live flow run whose start was imported.
/// Each case stands an imported event in by giving it the record the import saves with it, a
/// <see cref="LedgerLine"/> naming another origin, and sits beside a local case the daemon does act on.
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
            await MarkImportedAsync(store, peer, enteredByPeer);

            // A peer's flow run, live on its parcel, and a local one beside it.
            Guid peerParcel = await AtBuildAsync(parcels, "Building on a peer");
            startedByPeer = (await flowRuns.StartAsync(Principal.LocalOwner, Project, peerParcel, "build", None)).Id;
            await MarkImportedAsync(store, peer, peerParcel, startedByPeer);
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

    private static async Task<Guid> AtBuildAsync(ParcelService parcels, string title)
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", title, null, None);
        await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
        await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
        return parcel.Id;
    }

    /// <summary>Gives every event of <paramref name="streams"/> the record the import keeps for a line of <paramref name="origin"/>.</summary>
    private static async Task MarkImportedAsync(IDocumentStore store, Guid origin, params Guid[] streams)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        long sequence = 0;
        foreach (Guid stream in streams)
        {
            foreach (IEvent entry in await session.Events.FetchStreamAsync(stream, token: None))
            {
                session.Store(new LedgerLine { Id = entry.Id, Origin = origin, Sequence = ++sequence });
            }
        }

        await session.SaveChangesAsync(None);
    }

    private static async Task<int> StreamLengthAsync(IDocumentStore store, Guid stream)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return (await session.Events.FetchStreamAsync(stream, token: None)).Count;
    }
}
