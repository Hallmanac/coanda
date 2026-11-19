using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using RegisteredProject = Coanda.Core.Projects.Project;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Tests.Daemon.FlowRuns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Coanda.Tests.Daemon.Triggers.TriggerFiring;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// A published trigger on an external event whose target creates a parcel: which triggers fire, what the parcel
/// and its flow run record, and what is refused before anything is created.
/// </summary>
public sealed class ExternalEventTriggerTests
{
    private const string Other = "beta";
    private const string Module = "tracker";

    private static ModuleArtifact Tracker(params TriggerDefinition[] triggers) =>
        ModuleArtifact.Of(Module, "1.0.0", [.. triggers.Select(ModuleDefinition.FromTrigger)]);

    private static async Task SwitchOnTrackerAsync(Seeded seeded, string project)
    {
        ProjectService projects = ProjectServices.Software(seeded.Store, TimeProvider.System);
        RegisteredProject registered = await projects.ShowAsync(Principal.LocalOwner, project, None) ?? throw new InvalidOperationException($"No project '{project}'.");
        await projects.SetModulesAsync(Principal.LocalOwner, project, [.. registered.Modules, Module], None);
    }

    [Fact]
    public async Task A_trigger_published_in_project_owner_or_module_scope_fires_and_a_drafted_or_retired_one_does_not()
    {
        using var temp = new TemporaryHome();
        var events = new Dictionary<string, Guid>();
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Creating("in-project", "opened", Fields(("repo", "a"))));
            await TriggerAsync(seeded.Store, Creating("Of-Owner", "opened", Fields(("repo", "b"))), DefinitionScope.Owner);
            await DefinitionsOf(seeded.Store).ImportAsync(Tracker(Creating("of-module", "opened", Fields(("repo", "c")))), None);
            await SwitchOnTrackerAsync(seeded, Project);
            await TriggerAsync(seeded.Store, Creating("drafted", "opened", Fields(("repo", "d"))), publish: false);
            Definition retired = await TriggerAsync(seeded.Store, Creating("retired", "opened", Fields(("repo", "e"))));
            await DefinitionsOf(seeded.Store).RetireAsync(Principal.LocalOwner, Project, retired.Id, None);

            foreach (string repo in new[] { "a", "b", "c", "d", "e" })
            {
                events[repo] = (await seeded.RecordAsync("opened", Fields(("repo", repo), ("title", $"Opened in {repo}")))).Id;
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();

        (string Repo, string Principal)[] fired =
        [
            ("a", $"trigger:project:alpha/in-project:event:{events["a"]}"),
            ("b", $"trigger:owner/of-owner:event:{events["b"]}"),
            ("c", $"trigger:module:tracker/of-module:event:{events["c"]}"),
        ];
        foreach ((string repo, string principal) in fired)
        {
            ParcelView view = await ParcelTitledAsync(client, $"Opened in {repo}");
            Parcel parcel = await parcels.ShowAsync(Principal.LocalOwner, Project, view.Id, None) ?? throw new InvalidOperationException("No parcel.");
            Assert.Equal(("task", principal, principal), (parcel.TypeName, parcel.CreatedBy, parcel.Origin));
            Assert.Empty(parcel.Relations);
            FlowRunView flowRun = Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, view.Id));
            Assert.Equal((Hold.Name, principal), (flowRun.FlowName, flowRun.StartedBy));
        }

        // a drafted and a retired trigger create nothing
        Assert.Equal(
            ["Opened in a", "Opened in b", "Opened in c"],
            (await CreatedAsync(client)).Select(p => p.Title).Order(StringComparer.Ordinal));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_module_scope_trigger_fires_in_a_project_its_module_is_switched_on_for_and_not_in_one_it_is_not()
    {
        using var temp = new TemporaryHome();
        await SeedAsync(temp, async seeded =>
        {
            await RegisteredProjects.AddAsync(seeded.Store, Other, None);
            await DefinitionsOf(seeded.Store).ImportAsync(Tracker(Creating("of-module", "opened", Fields())), None);
            await SwitchOnTrackerAsync(seeded, Project);
            await seeded.Triggers.RecordEventAsync(Principal.LocalOwner, Other, "opened", "in-beta", null, Fields(("title", "Opened in beta")), None);
            await seeded.RecordAsync("opened", Fields(("title", "Opened in alpha")));
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        // The loop handles every project's record in one order, so beta's earlier event was handled too.
        await ParcelTitledAsync(client, "Opened in alpha");
        Assert.Empty(await client.GetAsync<List<ParcelView>>($"/parcels?project={Other}", None));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task When_a_project_scope_and_a_module_scope_trigger_share_a_name_only_the_projects_fires()
    {
        using var temp = new TemporaryHome();
        Guid recorded = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await DefinitionsOf(seeded.Store).ImportAsync(Tracker(Creating("shared", "opened", Fields())), None);
            await SwitchOnTrackerAsync(seeded, Project);
            await TriggerAsync(seeded.Store, Creating("shared", "opened", Fields()));
            recorded = (await seeded.RecordAsync("opened", Fields(("title", "Opened once")))).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        ParcelView parcel = Assert.Single(await CreatedAsync(client));
        Assert.Equal($"trigger:project:alpha/shared:event:{recorded}", parcel.CreatedBy);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_create_target_that_cannot_be_met_creates_nothing_logs_the_trigger_and_why_and_the_loop_goes_on()
    {
        using var temp = new TemporaryHome();
        await SeedAsync(temp, async seeded =>
        {
            await FlowAsync(seeded.Store, Waiting("ideas-only", "approved") with { Accepts = ["idea"] });
            await TriggerAsync(seeded.Store, Creating("no-type", "no-type", Fields(), type: "no-such-type"));
            await TriggerAsync(seeded.Store, Creating("no-flow", "no-flow", Fields(), flow: "no-such-flow"));
            await TriggerAsync(seeded.Store, Creating("not-accepted", "not-accepted", Fields(), flow: "ideas-only"));
            await TriggerAsync(seeded.Store, Creating("untitled", "untitled", Fields(), titleField: "heading"));
            foreach (string kind in new[] { "no-type", "no-flow", "not-accepted", "untitled" })
            {
                await seeded.RecordAsync(kind, Fields(("title", $"Refused by {kind}")));
            }

            await MarkAsync(seeded.Store, "Marker");
        });

        var logs = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { LoggerProvider = logs }, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Empty(await CreatedAsync(client));
        (string Trigger, string Reason)[] refusals =
        [
            ("trigger:project:alpha/no-type", "parcel type 'no-such-type' does not resolve"),
            ("trigger:project:alpha/no-flow", "'no-such-flow' is not a published flow"),
            ("trigger:project:alpha/not-accepted", "accepts idea, not the parcel type 'task'"),
            ("trigger:project:alpha/untitled", "the field 'heading'"),
        ];
        foreach ((string trigger, string reason) in refusals)
        {
            Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains(trigger, StringComparison.Ordinal)
                && entry.Message.Contains(reason, StringComparison.Ordinal));
        }

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_over_cap_title_creates_no_parcel_and_starts_no_flow_run_and_the_refusal_names_the_trigger_the_event_and_the_field()
    {
        using var temp = new TemporaryHome();
        Guid recorded = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Creating("long", "opened", Fields()));
            recorded = (await seeded.RecordAsync("opened", Fields(("title", new string('x', ParcelCaps.TitleLength + 1))))).Id;
            await seeded.RecordAsync("opened", Fields(("title", new string('y', ParcelCaps.TitleLength))));
            await MarkAsync(seeded.Store, "Marker");
        });

        var logs = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { LoggerProvider = logs }, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        // the title at the cap is kept whole, and the one over it is neither created nor shortened
        ParcelView atCap = Assert.Single(await CreatedAsync(client));
        Assert.Equal(new string('y', ParcelCaps.TitleLength), atCap.Title);
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, atCap.Id));
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("trigger:project:alpha/long", StringComparison.Ordinal)
            && entry.Message.Contains($"external event {Ids.Short(recorded)}", StringComparison.Ordinal)
            && entry.Message.Contains("the field 'title'", StringComparison.Ordinal)
            && entry.Message.Contains($"{ParcelCaps.TitleLength + 1} characters", StringComparison.Ordinal));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_event_another_node_recorded_creates_no_parcel_here()
    {
        using var temp = new TemporaryHome();
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Creating("opened", "opened", Fields()));
            ExternalEvent imported = await seeded.RecordAsync("opened", Fields(("title", "Recorded by a peer")));
            await ImportAsync(seeded.Store, imported.Id);
            await seeded.RecordAsync("opened", Fields(("title", "Recorded here")));
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal(["Recorded here"], (await CreatedAsync(client)).Select(p => p.Title));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_subscription_that_fails_on_an_event_leaves_the_events_trigger_to_create_its_parcel()
    {
        using var temp = new TemporaryHome();
        Guid eventId = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await StoredSubscriptions.RecordAsync(
                seeded.Store, Project, new Principal("teammate", Role.Member), new Trigger("opened", Fields()), "task", "title", Hold.Name, None);
            await TriggerAsync(seeded.Store, Creating("opened", "opened", Fields(), titleField: "summary"));
            eventId = (await seeded.RecordAsync("opened", Fields(("title", "By the subscription"), ("summary", "By the trigger")))).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        int failed = 0;
        Task Fail(Subscription subscription, CancellationToken token)
        {
            Interlocked.Increment(ref failed);
            throw new InvalidOperationException("The store refused the subscription's write.");
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { SubscriptionFiring = Fail }, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal(1, Volatile.Read(ref failed));
        ParcelView created = Assert.Single(await CreatedAsync(client));
        Assert.Equal(("By the trigger", $"trigger:project:alpha/opened:event:{eventId}"), (created.Title, created.CreatedBy));
        Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, created.Id));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_event_whose_parcel_another_node_already_created_for_the_trigger_creates_nothing_here()
    {
        using var temp = new TemporaryHome();
        Guid peers = Guid.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Creating("opened", "opened", Fields()));
            Guid eventId = (await seeded.RecordAsync("opened", Fields(("title", "Answered by a peer")))).Id;

            // The peer's parcel for the same trigger and the same event, its origin written from the parts.
            string origin = $"trigger:project:alpha/opened:event:{eventId}";
            peers = (await seeded.Parcels.AddAsync(new Principal(origin, Role.Member), Project, "task", "The peer's", null, origin, None)).Id;
            await ImportAsync(seeded.Store, peers);
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal([peers], (await CreatedAsync(client)).Select(p => p.Id));
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, peers));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_event_handled_again_after_its_parcel_was_created_here_starts_the_flow_on_that_parcel_once()
    {
        using var temp = new TemporaryHome();
        Guid created = Guid.Empty;
        string origin = string.Empty;
        await SeedAsync(temp, async seeded =>
        {
            await TriggerAsync(seeded.Store, Creating("opened", "opened", Fields()));
            Guid eventId = (await seeded.RecordAsync("opened", Fields(("title", "Stopped before its start")))).Id;

            // What a stop between the creation and the start leaves: this node's parcel for the event, with no flow run.
            origin = $"trigger:project:alpha/opened:event:{eventId}";
            created = (await seeded.Parcels.AddAsync(new Principal(origin, Role.Member), Project, "task", "Stopped before its start", null, origin, None)).Id;
            await MarkAsync(seeded.Store, "Marker");
        });

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        await ParcelTitledAsync(client, "Marker");

        Assert.Equal([created], (await CreatedAsync(client)).Select(p => p.Id));
        FlowRunView started = Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, created));
        Assert.Equal((Hold.Name, origin), (started.FlowName, started.StartedBy));
        await daemon.StopAsync(None);
    }
}
