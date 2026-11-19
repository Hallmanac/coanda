using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Transport;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Coanda.Tests.Daemon.Triggers.TriggerFiring;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// A published trigger whose target acts on the parcel an external event names: it starts its flow there
/// where the parcel's claims let this node, as a binding's entry does, and is refused, keeping nothing to
/// start later, when the parcel cannot take the flow.
/// </summary>
public sealed class EventParcelTriggerTests
{
    private static readonly TriggerDefinition Review = OnEventParcel("review", "review-requested", Fields());

    /// <summary>The principal the trigger starts as for <paramref name="eventId"/>, written from its parts: scope, project, name, and the event's id.</summary>
    private static string StartedFor(Guid eventId) => $"trigger:project:alpha/review:event:{eventId}";

    [Theory]
    [InlineData(Standing.Held, false, true)]
    [InlineData(Standing.Held, true, true)]
    [InlineData(Standing.Unclaimed, false, true)]
    [InlineData(Standing.Unclaimed, true, false)]
    [InlineData(Standing.Lost, false, false)]
    [InlineData(Standing.Lost, true, false)]
    public async Task An_event_starts_the_flow_on_its_parcel_where_the_claims_let_this_node_and_creates_no_parcel(Standing standing, bool imported, bool starts)
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid eventId = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Review);
            parcelId = await TaskAsync(seeded.Store, "Asked for a review");
            await seeded.StandAsync(parcelId, standing);
            eventId = (await seeded.RecordAsync(Review.EventKind, Fields(("title", "Not a title for anything")), parcelId)).Id;
            if (imported)
            {
                await ImportAsync(seeded.Store, eventId);
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(client, Project, parcelId);
        if (starts)
        {
            FlowRunView started = Assert.Single(flowRuns);
            Assert.Equal((Hold.Name, StartedFor(eventId)), (started.FlowName, started.StartedBy));
        }
        else
        {
            Assert.Empty(flowRuns);
        }

        Assert.Equal([parcelId], (await CreatedAsync(client)).Select(p => p.Id));
        await daemon.StopAsync(None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_event_on_a_parcel_whose_claim_is_not_yet_held_starts_the_flow_once_the_claim_is_held(bool imported)
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid eventId = Guid.Empty;
        long through = 0;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Review);
            parcelId = await TaskAsync(seeded.Store, "Claimed, not yet held");
            through = await seeded.ClaimAsync(parcelId, fetched: false);
            eventId = (await seeded.RecordAsync(Review.EventKind, Fields(), parcelId)).Id;
            if (imported)
            {
                await ImportAsync(seeded.Store, eventId);
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore running = daemon.Services.GetRequiredService<IDocumentStore>();
        await ParcelTitledAsync(client, "Marker");
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, parcelId));

        // A fetch after the push holds the parcel, and the event held back starts the flow.
        await LedgerProgress.AdvanceAsync(running, Project, LedgerProgress.FetchedId, through, None);
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                await using IQuerySession session = running.QuerySession(Project);
                return await session.LoadAsync<HeldBackEntries>(parcelId, None) is null;
            },
            "the held back event is cleared");
        FlowRunView started = Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, parcelId));
        Assert.Equal(StartedFor(eventId), started.StartedBy);

        // Handled once more after a later entry, the event starts nothing again.
        await MarkAsync(running, "Marker again");
        await ParcelTitledAsync(client, "Marker again");
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, parcelId));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_event_whose_parcel_cannot_take_the_flow_starts_nothing_logs_why_and_keeps_nothing_to_start_later()
    {
        using var temp = new TemporaryHome();
        Guid closed = Guid.Empty;
        Guid busy = Guid.Empty;
        Guid busyRun = Guid.Empty;
        Guid idea = Guid.Empty;
        Guid elsewhere = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await RegisteredProjects.AddAsync(seeded.Store, "beta", None);
            await FlowAsync(seeded.Store, Waiting("ideas-only", "approved") with { Accepts = ["idea"] });
            await TriggerAsync(seeded.Store, OnEventParcel("no-parcel", "no-parcel", Fields()));
            await TriggerAsync(seeded.Store, OnEventParcel("elsewhere", "elsewhere", Fields()));
            await TriggerAsync(seeded.Store, OnEventParcel("closed", "closed", Fields()));
            await TriggerAsync(seeded.Store, OnEventParcel("busy", "busy", Fields()));
            await TriggerAsync(seeded.Store, OnEventParcel("not-accepted", "not-accepted", Fields(), flow: "ideas-only"));
            await TriggerAsync(seeded.Store, OnEventParcel("no-flow", "no-flow", Fields(), flow: "no-such-flow"));

            closed = await TaskAsync(seeded.Store, "Closed already");
            await seeded.Parcels.CloseAsync(Principal.LocalOwner, Project, closed, "completed", "Done before the event.", None);
            busy = await TaskAsync(seeded.Store, "Busy with a flow run");
            busyRun = (await seeded.FlowRuns.StartAsync(Principal.LocalOwner, Project, busy, Hold.Name, None)).Id;
            idea = await TaskAsync(seeded.Store, "Of a type the flow does not take");
            elsewhere = (await seeded.Parcels.AddAsync(Principal.LocalOwner, "beta", "task", "In another project", null, None)).Id;

            await seeded.RecordAsync("no-parcel", Fields());
            await seeded.RecordAsync("elsewhere", Fields(), elsewhere);
            await seeded.RecordAsync("closed", Fields(), closed);
            await seeded.RecordAsync("busy", Fields(), busy);
            await seeded.RecordAsync("not-accepted", Fields(), idea);
            await seeded.RecordAsync("no-flow", Fields(), idea);
            await MarkAsync(seeded.Store, "Marker");
        });

        var logs = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { LoggerProvider = logs }, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        (string Trigger, string Reason)[] refusals =
        [
            ("no-parcel", "names no parcel"),
            ("elsewhere", $"which is no parcel of project '{Project}'"),
            ("closed", "is closed (completed)"),
            ("busy", $"has flow run {Ids.Short(busyRun)} live"),
            ("not-accepted", "accepts idea, not the parcel type 'task'"),
            ("no-flow", "'no-such-flow' is not a published flow"),
        ];
        foreach ((string trigger, string reason) in refusals)
        {
            Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains($"trigger:project:alpha/{trigger}", StringComparison.Ordinal)
                && entry.Message.Contains(reason, StringComparison.Ordinal));
        }

        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, closed));
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, idea));
        Assert.Empty(await client.GetAsync<List<FlowRunView>>($"/flow-runs?project=beta&parcel={elsewhere}", None));

        // The busy parcel's flow run is released, and nothing was kept to start on it then.
        await daemon.Services.GetRequiredService<FlowRunService>().StopAsync(Principal.LocalOwner, Project, busyRun, "Released for the test.", None);
        await MarkAsync(daemon.Services.GetRequiredService<IDocumentStore>(), "Marker after the release");
        await ParcelTitledAsync(client, "Marker after the release");
        Assert.Equal([busyRun], (await FlowRunDriver.OfParcelAsync(client, Project, busy)).Select(f => f.Id));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_event_whose_flow_run_another_node_started_and_ended_starts_nothing_again()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid answered = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Review);
            parcelId = await TaskAsync(seeded.Store, "Answered by a peer");
            Guid eventId = (await seeded.RecordAsync(Review.EventKind, Fields(), parcelId)).Id;

            // The peer's flow run for that event, started as the trigger's principal and ended since.
            var principal = new Principal($"trigger:project:alpha/review:event:{eventId}", Role.Member);
            FlowRun peers = await seeded.FlowRuns.StartAsync(principal, Project, parcelId, Hold.Name, None);
            await seeded.FlowRuns.EndAsync(Principal.LocalOwner, Project, peers.Id, Hold.Outcome, None);
            await ImportAsync(seeded.Store, peers.Id);
            answered = peers.Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal([answered], (await FlowRunDriver.OfParcelAsync(client, Project, parcelId)).Select(f => f.Id));
        await daemon.StopAsync(None);
    }
}
