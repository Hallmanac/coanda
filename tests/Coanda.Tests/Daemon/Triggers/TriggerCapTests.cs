using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Transport;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.Logging;
using RegisteredProject = Coanda.Core.Projects.Project;
using static Coanda.Tests.Daemon.Triggers.TriggerFiring;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// The hourly cap a trigger that creates parcels carries: past it, in the hour before the loop handles an event, the
/// trigger creates no parcel in the project for that event, counted by the trigger's name over the parcels the
/// project's record holds. A parcel the count reads is stood in by one written with the trigger's origin, at a time a
/// shifted clock gives, before the daemon starts; its title starts with <see cref="CountedTitle"/>.
/// </summary>
public sealed class TriggerCapTests
{
    private const string Other = "beta";
    private const string Module = "tracker";
    private const string CountedTitle = "Counted";

    /// <summary>The clock of the machine, moved by <paramref name="by"/>.</summary>
    private sealed class Shifted(TimeSpan by) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + by;
    }

    private static TriggerDefinition Capped(TriggerDefinition trigger, int cap) => trigger with { HourlyCap = cap };

    /// <summary>
    /// A parcel of <paramref name="project"/> recorded as created <paramref name="by"/> from now by a firing of the
    /// trigger <paramref name="scope"/>/<paramref name="name"/> on an event no record holds, as the loop would create it.
    /// </summary>
    private static async Task<Parcel> CountedAsync(Seeded seeded, string name, TimeSpan by, string scope = "project:alpha", string project = Project)
    {
        string origin = $"trigger:{scope}/{name}:event:{Ids.New()}";
        return await new ParcelService(seeded.Store, new Shifted(by))
            .AddAsync(new Principal(origin, Role.Member), project, "task", $"{CountedTitle} for {name}", null, origin, None);
    }

    /// <summary>Gives the parcel's creation the record the import keeps for a line of a peer, imported at <paramref name="importedAt"/>.</summary>
    private static async Task ImportedAtAsync(IDocumentStore store, Guid parcelId, DateTimeOffset importedAt)
    {
        await using IDocumentSession session = store.LightweightSession(Project);
        IEvent creation = (await session.Events.FetchStreamAsync(parcelId, token: None))[0];
        session.Store(new LedgerLine { Id = creation.Id, Origin = Ids.New(), Sequence = 1, Hash = RandomHash(), ImportedAt = importedAt });
        await session.SaveChangesAsync(None);
    }

    /// <summary>The parcels of <paramref name="project"/> the loop created, other than the markers', the counted ones, and <paramref name="except"/>.</summary>
    private static async Task<List<ParcelView>> NewAsync(DaemonClient client, string project = Project, params Guid[] except) =>
        [.. (await client.GetAsync<List<ParcelView>>($"/parcels?project={project}", None))
            .Where(p => !p.Title.StartsWith("Marker", StringComparison.Ordinal)
                && !p.Title.StartsWith(CountedTitle, StringComparison.Ordinal)
                && !except.Contains(p.Id))];

    private static async Task SwitchOnTrackerAsync(Seeded seeded, string project)
    {
        ProjectService projects = ProjectServices.Software(seeded.Store, TimeProvider.System);
        RegisteredProject registered = await projects.ShowAsync(Principal.LocalOwner, project, None) ?? throw new InvalidOperationException($"No project '{project}'.");
        await projects.SetModulesAsync(Principal.LocalOwner, project, [.. registered.Modules, Module], None);
    }

    private static bool Dropped(RecordingLogger logs, string trigger, string project, int cap, string cause) =>
        logs.Entries.Any(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains(trigger, StringComparison.Ordinal)
            && entry.Message.Contains($"project {project}", StringComparison.Ordinal)
            && entry.Message.Contains($"hourly cap of {cap}", StringComparison.Ordinal)
            && entry.Message.Contains(cause, StringComparison.Ordinal));

    [Fact]
    public async Task A_trigger_at_its_cap_creates_nothing_for_a_further_event_and_logs_it_while_other_triggers_and_subscriptions_go_on()
    {
        using var temp = new TemporaryHome();
        List<Guid> events = [];
        List<Guid> tasks = [];
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Capped(Creating("Capped", "opened", Fields()), 2));
            await TriggerAsync(seeded.Store, Creating("beside", "opened", Fields(), titleField: "summary"));
            await TriggerAsync(seeded.Store, OnEventParcel("on-parcel", "opened", Fields()));
            await StoredSubscriptions.RecordAsync(
                seeded.Store, Project, new Principal("teammate", Role.Member), new Trigger("opened", Fields()), "task", "note", Hold.Name, None);
            for (int i = 1; i <= 3; i++)
            {
                Guid task = await TaskAsync(seeded.Store, $"Pinged {i}");
                tasks.Add(task);
                ExternalEvent recorded = await seeded.RecordAsync(
                    "opened", Fields(("title", $"Capped {i}"), ("summary", $"Beside {i}"), ("note", $"Subscribed {i}")), task);
                events.Add(recorded.Id);
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        var logs = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { LoggerProvider = logs }, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        List<ParcelView> created = await NewAsync(client, except: [.. tasks]);
        Assert.Equal(
            ["Beside 1", "Beside 2", "Beside 3", "Capped 1", "Capped 2", "Subscribed 1", "Subscribed 2", "Subscribed 3"],
            created.Select(p => p.Title).Order(StringComparer.Ordinal));
        foreach (ParcelView parcel in created)
        {
            Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id));
        }

        // the trigger acting on the event's parcel is held back by no cap
        foreach (Guid task in tasks)
        {
            Assert.Equal(Hold.Name, Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, task)).FlowName);
        }

        Assert.True(Dropped(logs, "trigger:project:alpha/capped", Project, 2, $"external event {Ids.Short(events[2])}"));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_trigger_on_an_end_at_its_cap_creates_nothing_for_a_further_end_while_the_follow_starts_on_both()
    {
        using var temp = new TemporaryHome();
        Guid first = Guid.Empty;
        Guid second = Guid.Empty;
        Guid firstEnd = Guid.Empty;
        Guid secondEnd = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Capped(OnEnd("evaluate", "source"), 1));
            first = await TaskAsync(seeded.Store, "Ended first");
            second = await TaskAsync(seeded.Store, "Ended second");
            firstEnd = await seeded.EndedAsync(first, Source);
            secondEnd = await seeded.EndedAsync(second, Source);
            await MarkAsync(seeded.Store, "Marker");
        });

        var logs = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { LoggerProvider = logs }, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        ParcelView created = Assert.Single(await NewAsync(client, except: [first, second]));
        Assert.Equal(($"trigger:project:alpha/evaluate:flow-run:{firstEnd}", "Ended first"), (created.CreatedBy, created.Title));
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, created.Id));
        await FlowRunDriver.FollowerAsync(client, Project, first, firstEnd, AfterSource.Name);
        await FlowRunDriver.FollowerAsync(client, Project, second, secondEnd, AfterSource.Name);
        Assert.True(Dropped(logs, "trigger:project:alpha/evaluate", Project, 1, $"the end of flow run {Ids.Short(secondEnd)}"));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_parcel_counts_while_its_creation_is_within_the_hour_before_or_ahead_of_this_nodes_clock()
    {
        using var temp = new TemporaryHome();
        await SeedAsync(temp, async seeded =>
        {
            foreach (string name in new[] { "aged", "recent", "ahead" })
            {
                await TriggerAsync(seeded.Store, Capped(Creating(name, name, Fields()), 1));
            }

            await CountedAsync(seeded, "aged", TimeSpan.FromMinutes(-70));
            await CountedAsync(seeded, "recent", TimeSpan.FromMinutes(-50));
            await CountedAsync(seeded, "ahead", TimeSpan.FromHours(2));
            foreach (string name in new[] { "aged", "recent", "ahead" })
            {
                await seeded.RecordAsync(name, Fields(("title", $"New for {name}")));
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        // only the trigger whose counted parcel is more than an hour old creates again
        ParcelView created = Assert.Single(await NewAsync(client));
        Assert.Equal("New for aged", created.Title);
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, created.Id));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_imported_parcel_recorded_as_created_ahead_of_its_import_counts_for_an_hour_from_the_import_and_not_after()
    {
        using var temp = new TemporaryHome();
        await SeedAsync(temp, async seeded =>
        {
            foreach (string name in new[] { "imported-recently", "imported-long-ago" })
            {
                await TriggerAsync(seeded.Store, Capped(Creating(name, name, Fields()), 1));
            }

            // Each recorded as created an hour after its import: the first imported half an hour ago, the second over an hour ago.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Parcel recently = await CountedAsync(seeded, "imported-recently", TimeSpan.FromMinutes(30));
            await ImportedAtAsync(seeded.Store, recently.Id, now.AddMinutes(-30));
            Parcel longAgo = await CountedAsync(seeded, "imported-long-ago", TimeSpan.FromMinutes(-1));
            await ImportedAtAsync(seeded.Store, longAgo.Id, now.AddMinutes(-61));
            foreach (string name in new[] { "imported-recently", "imported-long-ago" })
            {
                await seeded.RecordAsync(name, Fields(("title", $"New for {name}")));
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal(["New for imported-long-ago"], (await NewAsync(client)).Select(p => p.Title));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_count_is_per_project_and_a_project_trigger_shadowing_a_module_trigger_shares_its_count_closed_parcels_included()
    {
        using var temp = new TemporaryHome();
        await SeedAsync(temp, async seeded =>
        {
            await RegisteredProjects.AddAsync(seeded.Store, Other, None);
            DefinitionService definitions = DefinitionsOf(seeded.Store);
            Definition hold = await definitions.DraftAsync(
                Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Other, Hold.Name, Flow.CanonicalJson(Hold), Hold.Hash, null, None);
            await definitions.PublishAsync(Principal.LocalOwner, Other, hold.Id, None);
            ModuleArtifact tracker = ModuleArtifact.Of(Module, "1.0.0",
            [
                ModuleDefinition.FromTrigger(Capped(Creating("everywhere", "everywhere", Fields()), 1)),
                ModuleDefinition.FromTrigger(Capped(Creating("shadowed", "shadowed", Fields()), 1)),
            ]);
            await definitions.ImportAsync(tracker, None);
            await SwitchOnTrackerAsync(seeded, Project);
            await SwitchOnTrackerAsync(seeded, Other);
            await TriggerAsync(seeded.Store, Capped(Creating("Shadowed", "shadowed", Fields()), 1));

            // The module's trigger's parcel, from before the project's trigger shadowed it, since closed.
            Parcel closed = await CountedAsync(seeded, "shadowed", TimeSpan.FromMinutes(-10), scope: $"module:{Module}");
            await seeded.Parcels.CloseAsync(Principal.LocalOwner, Project, closed.Id, "completed", "Done before the shadowing.", None);

            await seeded.RecordAsync("shadowed", Fields(("title", "New for shadowed")));
            await seeded.Triggers.RecordEventAsync(Principal.LocalOwner, Other, "everywhere", "in-beta", null, Fields(("title", "Everywhere in beta")), None);
            await seeded.RecordAsync("everywhere", Fields(("title", "Everywhere in alpha")));
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal(["Everywhere in alpha"], (await NewAsync(client)).Select(p => p.Title));
        Assert.Equal(["Everywhere in beta"], (await NewAsync(client, Other)).Select(p => p.Title));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Republishing_a_trigger_with_another_cap_keeps_its_count_and_the_new_cap_applies()
    {
        using var temp = new TemporaryHome();
        await SeedAsync(temp, async seeded =>
        {
            TriggerDefinition trigger = Creating("Re-Published", "opened", Fields());
            await TriggerAsync(seeded.Store, Capped(trigger, 1));
            await CountedAsync(seeded, "re-published", TimeSpan.FromMinutes(-10));
            await TriggerAsync(seeded.Store, Capped(trigger, 2));
            await seeded.RecordAsync("opened", Fields(("title", "First")));
            await seeded.RecordAsync("opened", Fields(("title", "Second")));
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        // A reset count would create both, and a count of both versions' parcels twice would create neither.
        Assert.Equal(["First"], (await NewAsync(client)).Select(p => p.Title));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_event_handled_again_at_the_cap_starts_the_flow_on_the_parcel_it_created_and_creates_no_second()
    {
        using var temp = new TemporaryHome();
        Guid created = Guid.Empty;
        string origin = string.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Capped(Creating("opened", "opened", Fields()), 1));
            Guid eventId = (await seeded.RecordAsync("opened", Fields(("title", "Stopped before its start")))).Id;

            // What a stop between the creation and the start leaves: the parcel that brings the trigger to its cap.
            origin = $"trigger:project:alpha/opened:event:{eventId}";
            created = (await seeded.Parcels.AddAsync(new Principal(origin, Role.Member), Project, "task", "Stopped before its start", null, origin, None)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal([created], (await NewAsync(client)).Select(p => p.Id));
        FlowRunView started = Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, created));
        Assert.Equal((Hold.Name, origin), (started.FlowName, started.StartedBy));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_end_handled_again_at_the_cap_starts_the_flow_on_the_parcel_it_created_and_creates_no_second()
    {
        using var temp = new TemporaryHome();
        Guid parcelId = Guid.Empty;
        Guid ended = Guid.Empty;
        Guid created = Guid.Empty;
        string origin = string.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Capped(OnEnd("evaluate", "source"), 1));
            parcelId = await TaskAsync(seeded.Store, "Ended before a stop");
            ended = await seeded.EndedAsync(parcelId, Source);

            origin = $"trigger:project:alpha/evaluate:flow-run:{ended}";
            created = (await seeded.Parcels.AddAsync(
                new Principal(origin, Role.Member), Project, "task", "Ended before a stop", null, origin, new DerivedFrom(parcelId, ended), None)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal([created], (await NewAsync(client, except: [parcelId])).Select(p => p.Id));
        FlowRunView started = Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, created));
        Assert.Equal((Hold.Name, origin), (started.FlowName, started.StartedBy));
        await FlowRunDriver.FollowerAsync(client, Project, parcelId, ended, AfterSource.Name);
        await daemon.StopAsync(None);
    }
}
