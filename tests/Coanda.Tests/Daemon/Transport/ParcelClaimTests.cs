using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Identity.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Transport;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// Two node homes that claim one parcel without seeing each other's claim, over one stand-in remote: once
/// each has imported the other's claim both name the same winner, the lowest line hash, the loser's flow run
/// on the parcel is stopped, and the winner's own flow run stays the parcel's live one and is adopted at a restart.
/// </summary>
public sealed class ParcelClaimTests
{
    private const string Alpha = "alpha";
    private static readonly CancellationToken None = LedgerNode.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Two_nodes_claiming_one_parcel_unseen_name_one_winner_and_the_loser_stops_its_flow_run(bool firstImportsFirst, bool loserStopsBeforeImporting)
    {
        await using LedgerNode first = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode second = await LedgerNode.StartAsync();
        Guid parcel = await SharedParcelAsync(first, second);

        // Each node starts its own flow run on the parcel while it is unclaimed, then claims it without having
        // seen the other's claim, and pushes; neither fetches yet.
        FlowRun firstRun = await Runs(first).StartAsync(Owner, Alpha, parcel, SoftwareFlows.Build.Name, None);
        FlowRun secondRun = await Runs(second).StartAsync(Owner, Alpha, parcel, SoftwareFlows.Build.Name, None);
        foreach (LedgerNode node in new[] { first, second })
        {
            Assert.True((await Parcels(node).ClaimAsync(Owner, Alpha, parcel, node.Identity.NodeId, None)).Recorded);
            await PublishAsync(node);
        }

        // The winner is the claim on the lower line hash, which the test reads from each node's own line.
        bool firstWins = string.CompareOrdinal(await ClaimHashAsync(first, parcel), await ClaimHashAsync(second, parcel)) < 0;
        (LedgerNode winner, LedgerNode loser) = firstWins ? (first, second) : (second, first);
        (FlowRun won, FlowRun lost) = firstWins ? (firstRun, secondRun) : (secondRun, firstRun);

        if (loserStopsBeforeImporting)
        {
            await Runs(loser).StopAsync(Owner, Alpha, lost.Id, "Taken by hand", None);
        }

        foreach (LedgerNode node in firstImportsFirst ? new[] { first, second } : [second, first])
        {
            await ImportAsync(node);
        }

        // What the loser recorded on importing, its stop and its release among it, reaches the winner.
        await PublishAsync(loser);
        await ImportAsync(winner);

        Assert.IsType<ClaimStanding.Held>(await StandingAsync(winner, parcel));
        Assert.Equal(new ClaimStanding.Lost(winner.Identity.NodeId), await StandingAsync(loser, parcel));

        FlowRun stopped = await Runs(loser).ShowAsync(Owner, Alpha, lost.Id, None) ?? throw new InvalidOperationException("No losing flow run.");
        Assert.Equal(FlowRunStatus.Stopped, stopped.Status);
        if (!loserStopsBeforeImporting)
        {
            Assert.Contains($"Node {winner.Identity.NodeId}'s claim", stopped.StopReason, StringComparison.Ordinal);
        }

        Assert.Equal(FlowRunStatus.Stopped, (await Runs(winner).ShowAsync(Owner, Alpha, lost.Id, None))?.Status);
        Assert.Equal(won.Id, (await Parcels(winner).ShowAsync(Owner, Alpha, parcel, None))?.LiveFlowRunId);
        Assert.Equal(FlowRunStatus.Running, (await Runs(winner).ShowAsync(Owner, Alpha, won.Id, None))?.Status);

        // A claim made again records nothing, pushes and fetches again, and says the same.
        Assert.Equal((false, ParcelClaimResult.Held, (Guid?)null), Of(await winner.Claimant.ClaimAsync(Owner, Alpha, parcel, None)));
        Assert.Equal((false, ParcelClaimResult.Lost, (Guid?)winner.Identity.NodeId), Of(await loser.Claimant.ClaimAsync(Owner, Alpha, parcel, None)));

        // Restarted as a daemon, the winner adopts its own flow run, and the loser's stays as it was.
        int wonEvents = await StreamLengthAsync(winner.Store, won.Id);
        int lostEvents = await StreamLengthAsync(winner.Store, lost.Id);
        await winner.CloseStoreAsync();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>());
        await using RunningDaemon daemon = await Daemons.StartAsync(winner.Home, new DaemonOptions { Executor = executor }, None);
        IDocumentStore running = daemon.Services.GetRequiredService<IDocumentStore>();
        await FlowRunDriver.UntilAsync(async () => await StreamLengthAsync(running, won.Id) > wonEvents, "the winner's flow run is advanced");
        Assert.Equal(lostEvents, await StreamLengthAsync(running, lost.Id));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_daemon_started_on_a_node_that_stored_a_winning_claim_and_did_not_yet_stop_its_flow_run_stops_it_and_does_not_advance_it()
    {
        await using LedgerNode first = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode second = await LedgerNode.StartAsync();
        Guid parcel = await SharedParcelAsync(first, second);

        FlowRun firstRun = await Runs(first).StartAsync(Owner, Alpha, parcel, SoftwareFlows.Build.Name, None);
        FlowRun secondRun = await Runs(second).StartAsync(Owner, Alpha, parcel, SoftwareFlows.Build.Name, None);
        foreach (LedgerNode node in new[] { first, second })
        {
            Assert.True((await Parcels(node).ClaimAsync(Owner, Alpha, parcel, node.Identity.NodeId, None)).Recorded);
            await PublishAsync(node);
        }

        bool firstWins = string.CompareOrdinal(await ClaimHashAsync(first, parcel), await ClaimHashAsync(second, parcel)) < 0;
        (LedgerNode winner, LedgerNode loser) = firstWins ? (first, second) : (second, first);
        FlowRun lost = firstWins ? secondRun : firstRun;

        // The loser stores the winning claim and stops before its import stops the flow run it lost.
        await ImportAsync(winner);
        LedgerImportReport report = await loser.ImportWithoutYield.ImportAsync(Alpha, None);
        Assert.Empty(report.Failures);
        Assert.Equal(new ClaimStanding.Lost(winner.Identity.NodeId), await StandingAsync(loser, parcel));
        Assert.Equal(FlowRunStatus.Running, (await Runs(loser).ShowAsync(Owner, Alpha, lost.Id, None))?.Status);

        // Restarted as a daemon with no import loop, the loser stops it before advancing anything.
        int lostEvents = await StreamLengthAsync(loser.Store, lost.Id);
        await loser.CloseStoreAsync();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>());
        await using RunningDaemon daemon = await Daemons.StartAsync(loser.Home, new DaemonOptions { Executor = executor }, None);
        IDocumentStore running = daemon.Services.GetRequiredService<IDocumentStore>();
        FlowRun stopped = await new FlowRunService(running, TimeProvider.System).ShowAsync(Owner, Alpha, lost.Id, None)
            ?? throw new InvalidOperationException("No losing flow run.");
        Assert.Equal(FlowRunStatus.Stopped, stopped.Status);
        Assert.Contains($"Node {winner.Identity.NodeId}'s claim", stopped.StopReason, StringComparison.Ordinal);

        // Its stream holds the stop and nothing an advance would have recorded.
        await using (IQuerySession session = running.QuerySession(Alpha))
        {
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(lost.Id, token: None);
            Assert.All(events.Skip(lostEvents), e => Assert.IsType<FlowRunStopped>(e.Data));
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_claim_on_one_node_is_held_once_pushed_and_fetched_and_a_peer_that_has_seen_it_is_refused_a_claim_and_a_start()
    {
        await using LedgerNode first = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode second = await LedgerNode.StartAsync();
        Guid parcel = await SharedParcelAsync(first, second);

        Assert.Equal((true, ParcelClaimResult.Held, (Guid?)null), Of(await first.Claimant.ClaimAsync(Owner, Alpha, parcel, None)));
        await ImportAsync(second);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => second.Claimant.ClaimAsync(Owner, Alpha, parcel, None));
        Assert.Contains($"already claimed by node {first.Identity.NodeId}", refused.Message, StringComparison.Ordinal);

        // A start on the parcel the peer holds is refused here, a person's and the trigger loop's alike.
        foreach (Principal principal in new[] { Owner, new Principal($"binding:task/build/build:entry:{Ids.New()}", Role.Agent) })
        {
            DomainException start = await Assert.ThrowsAsync<DomainException>(() => Runs(second).StartAsync(principal, Alpha, parcel, SoftwareFlows.Build.Name, None));
            Assert.Contains($"node {first.Identity.NodeId}'s claim wins it", start.Message, StringComparison.Ordinal);
        }

        Assert.Equal(FlowRunStatus.Running, (await Runs(first).StartAsync(Owner, Alpha, parcel, SoftwareFlows.Build.Name, None)).Status);
    }

    [Fact]
    public async Task A_claim_whose_push_fails_is_recorded_and_not_yet_held_with_the_pushs_reason_and_a_start_says_so()
    {
        await using LedgerNode node = await LedgerNode.StartAsync();
        await node.AddProjectAsync(Alpha, withRemote: false);
        Parcel parcel = await AtBuildAsync(node);

        ParcelClaimResult claimed = await node.Claimant.ClaimAsync(Owner, Alpha, parcel.Id, None);

        Assert.Equal((true, ParcelClaimResult.NotYetHeld, (Guid?)null), Of(claimed));
        Assert.Contains("push: ", claimed.Reason, StringComparison.Ordinal);
        DomainException start = await Assert.ThrowsAsync<DomainException>(() => Runs(node).StartAsync(Owner, Alpha, parcel.Id, SoftwareFlows.Build.Name, None));
        Assert.Contains("this node's own claim on it is not yet held", start.Message, StringComparison.Ordinal);

        // Once the remote exists the same command records nothing new and holds the parcel.
        await node.MakeRemoteAsync(Alpha);
        Assert.Equal((false, ParcelClaimResult.Held, (Guid?)null), Of(await node.Claimant.ClaimAsync(Owner, Alpha, parcel.Id, None)));
    }

    [Fact]
    public async Task A_claim_is_not_held_while_this_node_trusts_no_peer_line_and_is_held_once_it_does()
    {
        await using LedgerNode first = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode second = await LedgerNode.StartAsync();
        await first.RecordRootAsync(Alpha);
        await first.RecordAsync(Alpha, new NodeVouched(second.Identity.NodeId, second.Identity.Key.PublicKey, DateTimeOffset.UtcNow));
        await PublishAsync(first);

        // The second node fetches the first's ref with no root key record, so it can read none of a peer's claims.
        Project project = await first.ProjectAsync(Alpha);
        await second.AddProjectAsync(Alpha, project.Id, [first.RemotePath(Alpha)]);
        Parcel parcel = await AtBuildAsync(second);

        ParcelClaimResult claimed = await second.Claimant.ClaimAsync(Owner, Alpha, parcel.Id, None);

        Assert.Equal((true, ParcelClaimResult.NotYetHeld, (Guid?)null), Of(claimed));
        Assert.Contains("no root key record", claimed.Reason, StringComparison.Ordinal);
        DomainException start = await Assert.ThrowsAsync<DomainException>(() => Runs(second).StartAsync(Owner, Alpha, parcel.Id, SoftwareFlows.Build.Name, None));
        Assert.Contains("this node's own claim on it is not yet held", start.Message, StringComparison.Ordinal);

        // Once the root is trusted the peer's lines are read, and the same command holds the parcel.
        await second.TrustRootAsync(Alpha, first.Identity.Key.PublicKey);
        Assert.Equal((false, ParcelClaimResult.Held, (Guid?)null), Of(await second.Claimant.ClaimAsync(Owner, Alpha, parcel.Id, None)));
    }

    [Fact]
    public async Task A_claim_is_not_held_while_a_peers_ref_cannot_be_read_and_is_weighed_against_its_claim_once_it_can()
    {
        await using LedgerNode first = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode second = await LedgerNode.StartAsync();
        Guid parcel = await SharedParcelAsync(first, second);
        Assert.Equal((true, ParcelClaimResult.Held, (Guid?)null), Of(await first.Claimant.ClaimAsync(Owner, Alpha, parcel, None)));

        // The fetch and the listing succeed, and the first node's ref, carrying its claim, cannot be read.
        second.ImportGit.Refuses = arguments => arguments.StartsWith("ls-tree", StringComparison.Ordinal);
        ParcelClaimResult claimed = await second.Claimant.ClaimAsync(Owner, Alpha, parcel, None);

        Assert.Equal((true, ParcelClaimResult.NotYetHeld, (Guid?)null), Of(claimed));
        Assert.IsType<ClaimStanding.NotYetHeld>(await StandingAsync(second, parcel));
        DomainException start = await Assert.ThrowsAsync<DomainException>(() => Runs(second).StartAsync(Owner, Alpha, parcel, SoftwareFlows.Build.Name, None));
        Assert.Contains("this node's own claim on it is not yet held", start.Message, StringComparison.Ordinal);

        // Once the ref is read, the first node's claim is weighed, and the lower line hash wins on both nodes.
        second.ImportGit.Refuses = null;
        ParcelClaimResult again = await second.Claimant.ClaimAsync(Owner, Alpha, parcel, None);
        bool firstWins = string.CompareOrdinal(await ClaimHashAsync(first, parcel), await ClaimHashAsync(second, parcel)) < 0;
        Assert.Equal(
            firstWins
                ? (false, ParcelClaimResult.Lost, (Guid?)first.Identity.NodeId)
                : (false, ParcelClaimResult.Held, (Guid?)null),
            Of(again));
    }

    [Fact]
    public async Task A_claim_is_refused_to_the_agent_role_and_on_a_closed_parcel()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        Parcel parcel = await AtBuildAsync(node);

        DomainException agent = await Assert.ThrowsAsync<DomainException>(
            () => node.Claimant.ClaimAsync(new Principal("coanda-triggers", Role.Agent), Alpha, parcel.Id, None));
        Assert.Contains("'parcel.claim' is not open to the Agent role", agent.Message, StringComparison.Ordinal);

        await Parcels(node).CloseAsync(Owner, Alpha, parcel.Id, "abandoned", "Not needed.", None);
        DomainException closed = await Assert.ThrowsAsync<DomainException>(() => node.Claimant.ClaimAsync(Owner, Alpha, parcel.Id, None));
        Assert.Contains("a closed parcel is claimed by nobody", closed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Makes <paramref name="first"/> the root of the project, vouching for <paramref name="second"/>, with a task at
    /// build that both nodes hold: the first pushes it, and the second registers the project at the same remote and imports it.
    /// </summary>
    private static async Task<Guid> SharedParcelAsync(LedgerNode first, LedgerNode second)
    {
        await first.RecordRootAsync(Alpha);
        await first.RecordAsync(Alpha, new NodeVouched(second.Identity.NodeId, second.Identity.Key.PublicKey, DateTimeOffset.UtcNow));
        await first.TrustRootAsync(Alpha, first.Identity.Key.PublicKey);
        Parcel parcel = await AtBuildAsync(first);
        await PublishAsync(first);

        Project project = await first.ProjectAsync(Alpha);
        await second.AddProjectAsync(Alpha, project.Id, [first.RemotePath(Alpha)]);
        await second.TrustRootAsync(Alpha, first.Identity.Key.PublicKey);
        await ImportAsync(second);
        Assert.NotNull(await Parcels(second).ShowAsync(Owner, Alpha, parcel.Id, None));
        return parcel.Id;
    }

    private static async Task<Parcel> AtBuildAsync(LedgerNode node)
    {
        ParcelService parcels = Parcels(node);
        Parcel parcel = await parcels.AddAsync(Owner, Alpha, "task", "Claimed", null, None);
        await parcels.BriefAsync(Owner, Alpha, parcel.Id, cancellationToken: None);
        return await parcels.MoveAsync(Owner, Alpha, parcel.Id, "build", None);
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

    /// <summary>The hash of the line carrying <paramref name="node"/>'s own claim on the parcel.</summary>
    private static async Task<string> ClaimHashAsync(LedgerNode node, Guid parcel)
    {
        IEvent claim = Assert.Single(
            await node.EventsAsync(Alpha),
            e => e.Data is ParcelClaimed claimed && claimed.ParcelId == parcel && claimed.NodeId == node.Identity.NodeId);
        return Assert.Single(await node.RecordsAsync(Alpha), r => r.Id == claim.Id).Hash;
    }

    private static async Task<ClaimStanding> StandingAsync(LedgerNode node, Guid parcelId)
    {
        await using IQuerySession session = node.Store.QuerySession(Alpha);
        Parcel parcel = await session.LoadAsync<Parcel>(parcelId, None) ?? throw new InvalidOperationException("No parcel.");
        return await ParcelClaims.StandingAsync(node.Store, session, parcel, None);
    }

    private static async Task<int> StreamLengthAsync(IDocumentStore store, Guid stream)
    {
        await using IQuerySession session = store.QuerySession(Alpha);
        return (await session.Events.FetchStreamAsync(stream, token: None)).Count;
    }

    private static (bool Recorded, string Result, Guid? Winner) Of(ParcelClaimResult result) => (result.Recorded, result.Result, result.Winner);

    private static ParcelService Parcels(LedgerNode node) => new(node.Store, TimeProvider.System);

    private static FlowRunService Runs(LedgerNode node) => new(node.Store, TimeProvider.System);
}
