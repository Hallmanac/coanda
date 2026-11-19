using Coanda.Cli;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Transport;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Coanda.Tests.Daemon.Triggers.TriggerFiring;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// A published trigger on a flow run's end: it creates a parcel derived from the ended flow run's parcel and
/// flow run, and starts its flow there, exactly where the claims on the ended flow run's parcel would let a
/// follow of the same end start here, whether or not that parcel is closed.
/// </summary>
public sealed class FlowRunEndTriggerTests
{
    /// <summary>Follows the end of <see cref="TriggerFiring.Source"/>, its flow filter spelled in another case than the flow's name.</summary>
    private static readonly TriggerDefinition Evaluate = OnEnd("evaluate", "Source");

    /// <summary>The principal and origin the trigger records for the end of <paramref name="flowRunId"/>, written from their parts.</summary>
    private static string CreatedFor(Guid flowRunId, string trigger = "evaluate") => $"trigger:project:alpha/{trigger}:flow-run:{flowRunId}";

    /// <summary>Asserts the trigger's parcel for the end of <paramref name="ended"/> on <paramref name="parcelId"/>, and its flow run.</summary>
    private static async Task AssertCreatedAsync(RunningDaemon daemon, DaemonClient client, Guid parcelId, Guid ended, string title, string trigger = "evaluate")
    {
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
        ParcelView view = Assert.Single(await CreatedAsync(client, parcelId), p => p.CreatedBy == CreatedFor(ended, trigger));
        Parcel created = await parcels.ShowAsync(Principal.LocalOwner, Project, view.Id, None) ?? throw new InvalidOperationException("No parcel.");
        Assert.Equal((title, CreatedFor(ended, trigger)), (created.Title, created.Origin));
        Assert.Equal([new ParcelRelation(RelationKinds.DerivedFrom, parcelId, ended)], created.Relations);
        FlowRunView flowRun = await FlowRunDriver.BoundAsync(client, Project, view.Id);
        Assert.Equal((Hold.Name, CreatedFor(ended, trigger)), (flowRun.FlowName, flowRun.StartedBy));
    }

    [Theory]
    [InlineData(Standing.Held, false, true)]
    [InlineData(Standing.Held, true, true)]
    [InlineData(Standing.Unclaimed, false, true)]
    [InlineData(Standing.Unclaimed, true, false)]
    [InlineData(Standing.Lost, false, false)]
    public async Task A_trigger_on_an_end_fires_exactly_where_a_follow_of_the_same_end_starts(Standing standing, bool imported, bool fires)
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid ended = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Evaluate);
            parcelId = await TaskAsync(seeded.Store, "Ended under its claims");
            if (standing == Standing.Held)
            {
                await seeded.StandAsync(parcelId, standing);
            }

            ended = await seeded.EndedAsync(parcelId, Source);
            if (standing == Standing.Lost)
            {
                await seeded.StandAsync(parcelId, standing);
            }

            if (imported)
            {
                await ImportAsync(seeded.Store, ended, after: await VersionAsync(seeded.Store, ended) - 1);
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        if (fires)
        {
            await AssertCreatedAsync(daemon, client, parcelId, ended, "Ended under its claims");
            Assert.Equal(AfterSource.Name, (await FlowRunDriver.FollowerAsync(client, Project, parcelId, ended, AfterSource.Name)).FlowName);
        }
        else
        {
            Assert.Empty(await CreatedAsync(client, parcelId));
            Assert.Equal([ended], (await FlowRunDriver.OfParcelAsync(client, Project, parcelId)).Select(f => f.Id));
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_end_on_a_parcel_whose_claim_is_not_yet_held_creates_nothing_until_the_claim_is_held_and_then_once()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid ended = Guid.Empty;
        long through = 0;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Evaluate);
            parcelId = await TaskAsync(seeded.Store, "Claimed after its end");
            ended = await seeded.EndedAsync(parcelId, Source);
            through = await seeded.ClaimAsync(parcelId, fetched: false);
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore running = daemon.Services.GetRequiredService<IDocumentStore>();
        await ParcelTitledAsync(client, "Marker");
        Assert.Empty(await CreatedAsync(client, parcelId));

        await LedgerProgress.AdvanceAsync(running, Project, LedgerProgress.FetchedId, through, None);
        await FlowRunDriver.FollowerAsync(client, Project, parcelId, ended, AfterSource.Name);
        await FlowRunDriver.UntilAsync(async () => (await CreatedAsync(client, parcelId)).Count > 0, "the trigger's parcel is created");
        await AssertCreatedAsync(daemon, client, parcelId, ended, "Claimed after its end");

        await MarkAsync(running, "Marker again");
        await ParcelTitledAsync(client, "Marker again");
        Assert.Single(await CreatedAsync(client, parcelId));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_trigger_fires_on_the_end_of_a_flow_that_closes_its_parcel()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid ended = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, OnEnd("after-close", Closing.Name));
            parcelId = await TaskAsync(seeded.Store, "Closed by its flow");
            ended = await seeded.EndedAsync(parcelId, Closing);
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Parcel closed = await daemon.Services.GetRequiredService<ParcelService>().ShowAsync(Principal.LocalOwner, Project, parcelId, None)
            ?? throw new InvalidOperationException("No parcel.");
        Assert.True(closed.IsClosed);
        await AssertCreatedAsync(daemon, client, parcelId, ended, "Closed by its flow", "after-close");
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_refused_trigger_on_an_end_leaves_the_follow_and_another_trigger_of_the_same_end_to_start()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid ended = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            // Resolution orders triggers by name, so the refused one is handled before the other.
            await TriggerAsync(seeded.Store, OnEnd("a-broken", "source", type: "no-such-type"));
            await TriggerAsync(seeded.Store, OnEnd("b-working", "source"));
            parcelId = await TaskAsync(seeded.Store, "Ended once");
            ended = await seeded.EndedAsync(parcelId, Source);
            await MarkAsync(seeded.Store, "Marker");
        });

        var logs = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { LoggerProvider = logs }, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        await AssertCreatedAsync(daemon, client, parcelId, ended, "Ended once", "b-working");
        Assert.Single(await CreatedAsync(client, parcelId));
        await FlowRunDriver.FollowerAsync(client, Project, parcelId, ended, AfterSource.Name);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("trigger:project:alpha/a-broken", StringComparison.Ordinal)
            && entry.Message.Contains("parcel type 'no-such-type' does not resolve", StringComparison.Ordinal));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_end_whose_parcel_another_node_already_created_for_the_trigger_creates_nothing_and_the_follow_still_starts()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid ended = Guid.Empty;
        Guid peers = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Evaluate);
            parcelId = await TaskAsync(seeded.Store, "Answered by a peer");
            ended = await seeded.EndedAsync(parcelId, Source);

            // The peer's parcel for the same trigger and the same end, its origin written from the parts.
            string origin = $"trigger:project:alpha/evaluate:flow-run:{ended}";
            peers = (await seeded.Parcels.AddAsync(
                new Principal(origin, Role.Member), Project, "task", "The peer's", null, origin, new DerivedFrom(parcelId, ended), None)).Id;
            await ImportAsync(seeded.Store, peers);
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        await FlowRunDriver.FollowerAsync(client, Project, parcelId, ended, AfterSource.Name);
        Assert.Equal([peers], (await CreatedAsync(client, parcelId)).Select(p => p.Id));
        // the peer's parcel is the peer's to start
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, peers));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_end_handled_again_after_its_parcel_was_created_here_starts_the_flow_on_that_parcel_once()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid ended = Guid.Empty;
        Guid created = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Evaluate);
            parcelId = await TaskAsync(seeded.Store, "Stopped between creation and start");
            ended = await seeded.EndedAsync(parcelId, Source);

            // What a stop between the creation and the start leaves: this node's parcel for the end, with no flow run.
            string origin = $"trigger:project:alpha/evaluate:flow-run:{ended}";
            created = (await seeded.Parcels.AddAsync(
                new Principal(origin, Role.Member), Project, "task", "Stopped between creation and start", null, origin, new DerivedFrom(parcelId, ended), None)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal([created], (await CreatedAsync(client, parcelId)).Select(p => p.Id));
        await AssertCreatedAsync(daemon, client, parcelId, ended, "Stopped between creation and start");
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, created));
        await daemon.StopAsync(None);
    }
}
