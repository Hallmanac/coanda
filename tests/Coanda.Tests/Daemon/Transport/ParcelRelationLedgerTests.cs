using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Identity.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Transport;
using Fisher;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// A relation one node records is exported on its ledger and imported by another as the parcel's other
/// events are, and the importing node lists it, even when it would have refused to record it itself.
/// </summary>
public sealed class ParcelRelationLedgerTests
{
    private const string Alpha = "alpha";
    private static readonly CancellationToken None = LedgerNode.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    [Fact]
    public async Task A_derived_from_and_a_blocked_by_recorded_on_one_node_are_listed_by_the_node_that_imports_them()
    {
        await using LedgerNode first = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode second = await LedgerNode.StartAsync();
        await TrustedPeersAsync(first, second);

        Parcel source = await Parcels(first).AddAsync(Owner, Alpha, "task", "Source", null, None);
        await Parcels(first).BriefAsync(Owner, Alpha, source.Id, cancellationToken: None);
        await Parcels(first).MoveAsync(Owner, Alpha, source.Id, "build", None);
        FlowRun flowRun = await new FlowRunService(first.Store, TimeProvider.System).StartAsync(Owner, Alpha, source.Id, "build", None);
        Parcel derived = await Parcels(first).AddAsync(Owner, Alpha, "task", "Derived", null, origin: null, new DerivedFrom(source.Id, flowRun.Id), None);
        Parcel blocked = await Parcels(first).RelateAsync(Owner, Alpha, source.Id, "blocked-by", derived.Id, null, None);
        await PublishAsync(first);

        await JoinAsync(first, second);

        Assert.Equal(derived.Relations, (await Parcels(second).ShowAsync(Owner, Alpha, derived.Id, None))?.Relations);
        Assert.Equal(blocked.Relations, (await Parcels(second).ShowAsync(Owner, Alpha, source.Id, None))?.Relations);
        Assert.Equal([new ParcelRelation("derived-from", source.Id, flowRun.Id)], derived.Relations);
        Assert.Equal([new ParcelRelation("blocked-by", derived.Id, null)], blocked.Relations);
    }

    [Fact]
    public async Task Two_nodes_that_each_record_one_blocked_by_unseen_both_list_both_once_they_import_each_other()
    {
        await using LedgerNode first = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode second = await LedgerNode.StartAsync();
        await TrustedPeersAsync(first, second);
        Parcel blocked = await Parcels(first).AddAsync(Owner, Alpha, "task", "Blocked", null, None);
        Parcel blocker = await Parcels(first).AddAsync(Owner, Alpha, "task", "Blocker", null, None);
        await PublishAsync(first);
        await JoinAsync(first, second);

        foreach (LedgerNode node in new[] { first, second })
        {
            await Parcels(node).RelateAsync(Owner, Alpha, blocked.Id, "blocked-by", blocker.Id, null, None);
            await PublishAsync(node);
        }

        await ImportAsync(first);
        await ImportAsync(second);

        var relation = new ParcelRelation("blocked-by", blocker.Id, null);
        foreach (LedgerNode node in new[] { first, second })
        {
            Assert.Equal([relation, relation], (await Parcels(node).ShowAsync(Owner, Alpha, blocked.Id, None))?.Relations);
            await Assert.ThrowsAsync<DomainException>(() => Parcels(node).RelateAsync(Owner, Alpha, blocked.Id, "blocked-by", blocker.Id, null, None));
        }
    }

    [Fact]
    public async Task An_imported_relation_to_a_parcel_this_node_does_not_hold_is_listed()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        Parcel parcel = await Parcels(node).AddAsync(Owner, Alpha, "task", "Imported into", null, None);
        Guid unheld = Ids.New();
        Guid flowRun = Ids.New();

        // The import appends a peer's event with no decider, as this does.
        await using (IDocumentSession session = node.Store.LightweightSession(Alpha))
        {
            session.Events.Append(parcel.Id, new ParcelRelationRecorded(parcel.Id, "derived-from", unheld, flowRun, "peer", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(None);
        }

        Assert.Equal([new ParcelRelation("derived-from", unheld, flowRun)], (await Parcels(node).ShowAsync(Owner, Alpha, parcel.Id, None))?.Relations);
    }

    private static async Task TrustedPeersAsync(LedgerNode first, LedgerNode second)
    {
        await first.RecordRootAsync(Alpha);
        await first.RecordAsync(Alpha, new NodeVouched(second.Identity.NodeId, second.Identity.Key.PublicKey, DateTimeOffset.UtcNow));
        await first.TrustRootAsync(Alpha, first.Identity.Key.PublicKey);
    }

    private static async Task JoinAsync(LedgerNode first, LedgerNode second)
    {
        Project project = await first.ProjectAsync(Alpha);
        await second.AddProjectAsync(Alpha, project.Id, [first.RemotePath(Alpha)]);
        await second.TrustRootAsync(Alpha, first.Identity.Key.PublicKey);
        await ImportAsync(second);
    }

    private static async Task PublishAsync(LedgerNode node)
    {
        LedgerPublication published = await node.Export.PublishAsync(Alpha, None);
        Assert.True(published.Published, string.Join("; ", published.Failures));
    }

    private static async Task ImportAsync(LedgerNode node)
    {
        LedgerImportReport report = await node.Import.ImportAsync(Alpha, None);
        Assert.Empty(report.Failures);
        Assert.All(report.Origins, o => Assert.Null(o.Fault));
    }

    private static ParcelService Parcels(LedgerNode node) => new(node.Store, TimeProvider.System);
}
