using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Coanda.Tests.Sdk.Contract;
using Microsoft.Extensions.DependencyInjection;
using NJsonSchema;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The watch requests the daemon sends its hosted modules when it starts: one per project a module is
/// switched on for, compiled from the triggers published before the start and those the module's
/// definitions folder ships, an empty set for a project the module is unconfigured for, and a module's
/// failing watch call costing nothing beyond that one request.
/// </summary>
public sealed class WatchRequestStartTests
{
    private const string Tracker = "tracker";
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private const string Gamma = "gamma";

    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly EventKindDescription[] TrackerKinds =
    [
        new("tracker/issue-opened", ["repo", "assignee"]),
        new("tracker/review-requested", ["reviewer"]),
    ];

    [Fact]
    public async Task A_module_is_sent_the_watches_of_its_shipped_and_its_published_triggers_for_each_project_it_is_switched_on_for_before_the_address_file()
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory shipped = StandInModuleDirectory.Write(Tracker, "1.0.0", ModuleDefinition.FromTrigger(SampleTriggers.IssueOpened));
        TriggerDefinition overriding = SampleTriggers.IssueOpened with { Filters = SampleTriggers.Filters(("repo", "other")) };
        TriggerDefinition drafted = SampleTriggers.ReviewRequested with { Name = "drafted-only", Filters = SampleTriggers.Filters(("reviewer", "sam")) };
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions(), None))
        {
            foreach (string project in new[] { Alpha, Beta, Gamma })
            {
                await RegisteredProjects.AddAsync(first, project, None);
            }

            await SwitchOnAsync(first, Alpha, Tracker);
            await SwitchOnAsync(first, Beta, Tracker);
            await PublishAsync(first, Alpha, SampleTriggers.ReviewRequested);
            await PublishAsync(first, Beta, overriding);
            await DraftAsync(first, Alpha, drafted);
            await first.StopAsync(None);
        }

        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        bool advertisedBeforeWatch = false;
        tracker.OnWatch = (_, _) =>
        {
            advertisedBeforeWatch |= File.Exists(temp.Home.DaemonInfoPath);
            return Task.CompletedTask;
        };
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Modules = new Dictionary<string, IModule> { [Tracker] = tracker }, ModulePaths = [shipped.Path] }, None);

        Assert.False(advertisedBeforeWatch);
        Assert.Equal([Alpha, Beta], tracker.WatchRequests.Select(r => r.Project));
        Assert.Collection(
            tracker.WatchRequests[0].Watches,
            w => AssertWatch(w, "tracker/issue-opened", ("assignee", "pat"), ("repo", "coanda")),
            w => AssertWatch(w, "tracker/review-requested", ("reviewer", "pat")));
        AssertWatch(Assert.Single(tracker.WatchRequests[1].Watches), "tracker/issue-opened", ("repo", "other"));
        JsonSchema schema = ModuleSchemas.Load("watch-request.schema.json");
        Assert.All(tracker.WatchDocuments, document => Assert.Empty(schema.Validate(document)));
    }

    [Fact]
    public async Task A_trigger_the_modules_describe_cannot_serve_is_left_out_with_a_log_line_and_the_projects_other_watches_are_sent()
    {
        using var temp = new TemporaryHome();
        TriggerDefinition unwatchable = SampleTriggers.ReviewRequested with { Name = "by-label", Filters = SampleTriggers.Filters(("label", "urgent")) };
        TriggerDefinition unlisted = SampleTriggers.ReviewRequested with { Name = "closed", EventKind = "tracker/issue-closed" };
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions(), None))
        {
            await RegisteredProjects.AddAsync(first, Alpha, None);
            await SwitchOnAsync(first, Alpha, Tracker);
            await PublishAsync(first, Alpha, SampleTriggers.ReviewRequested);
            await PublishAsync(first, Alpha, unwatchable);
            await PublishAsync(first, Alpha, unlisted);
            await first.StopAsync(None);
        }

        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        var log = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Modules = new Dictionary<string, IModule> { [Tracker] = tracker }, LoggerProvider = log }, None);

        WatchRequest request = Assert.Single(tracker.WatchRequests);
        AssertWatch(Assert.Single(request.Watches), "tracker/review-requested", ("reviewer", "pat"));
        string[] lines = [.. log.Entries.Select(e => e.Message).Where(m => m.StartsWith("Left trigger", StringComparison.Ordinal))];
        Assert.Equal(2, lines.Length);
        Assert.Contains(lines, l => l.Contains("trigger 'by-label'", StringComparison.Ordinal)
            && l.Contains($"project '{Alpha}'", StringComparison.Ordinal)
            && l.Contains("does not declare the field 'label' watchable", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("trigger 'closed'", StringComparison.Ordinal)
            && l.Contains($"project '{Alpha}'", StringComparison.Ordinal)
            && l.Contains("describes no event kind 'tracker/issue-closed'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_module_unconfigured_for_a_project_at_the_start_is_sent_an_empty_set_for_it()
    {
        using var temp = new TemporaryHome();
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions(), None))
        {
            await RegisteredProjects.AddAsync(first, Alpha, None);
            await SwitchOnAsync(first, Alpha, Tracker);
            await PublishAsync(first, Alpha, SampleTriggers.ReviewRequested);
            await first.StopAsync(None);
        }

        var tracker = new FeedModule(
            [], new SettingsSchema([], new CredentialProfileSetting(Required: true, DefaultsToNodeIdentity: false)), TrackerKinds);
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Modules = new Dictionary<string, IModule> { [Tracker] = tracker } }, None);

        WatchRequest request = Assert.Single(tracker.WatchRequests);
        Assert.Equal(Alpha, request.Project);
        Assert.Empty(request.Watches);
    }

    [Fact]
    public async Task A_watch_call_that_throws_stops_neither_the_start_nor_the_modules_other_requests_nor_another_modules_nor_its_feed()
    {
        const string Failing = "failing";
        const string Later = "later";
        using var temp = new TemporaryHome();
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions(), None))
        {
            await RegisteredProjects.AddAsync(first, Alpha, None);
            await RegisteredProjects.AddAsync(first, Beta, None);
            await SwitchOnAsync(first, Alpha, Failing, Later);
            await SwitchOnAsync(first, Beta, Failing);
            await first.StopAsync(None);
        }

        var failing = new FeedModule([CoreEventKind.ParcelEntered]);
        var later = new FeedModule([]);
        bool thrown = false;
        bool laterAfterThrow = false;
        failing.OnWatch = (_, _) =>
        {
            if (thrown)
            {
                return Task.CompletedTask;
            }

            thrown = true;
            throw new InvalidOperationException("The module cannot take its watches.");
        };
        later.OnWatch = (_, _) =>
        {
            laterAfterThrow = thrown;
            return Task.CompletedTask;
        };
        var log = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home,
            new DaemonOptions { Modules = new Dictionary<string, IModule> { [Failing] = failing, [Later] = later }, LoggerProvider = log },
            None);

        // The request that threw is sent again a second later; the start sent one for each project first.
        Assert.Equal([Alpha, Beta], failing.WatchRequests.Take(2).Select(r => r.Project));
        Assert.Equal(Alpha, Assert.Single(later.WatchRequests).Project);
        Assert.True(laterAfterThrow);
        Assert.All(failing.WatchRequests.Concat(later.WatchRequests), r => Assert.Empty(r.Watches));
        string line = Assert.Single(log.Entries, e => e.Message.StartsWith("The watch request", StringComparison.Ordinal)).Message;
        Assert.Contains($"project '{Alpha}'", line, StringComparison.Ordinal);
        Assert.Contains($"module '{Failing}'", line, StringComparison.Ordinal);

        Parcel parcel = await daemon.Services.GetRequiredService<ParcelService>().AddAsync(Principal.LocalOwner, Alpha, "task", "After the start", null, None);
        await FlowRunDriver.UntilAsync(() => failing.Events.Any(), "the failing module is delivered the parcel's entry");
        Assert.Equal(parcel.Id, Assert.IsType<FeedEvent.ParcelEntered>(Assert.Single(failing.Events)).ParcelId);
    }

    private static void AssertWatch(Watch watch, string kind, params (string Field, string Value)[] filters)
    {
        Assert.Equal(kind, watch.Kind);
        Assert.Equal([.. filters.Select(f => new WatchFilter(f.Field, f.Value))], watch.Filters);
    }

    private static Task SwitchOnAsync(RunningDaemon daemon, string project, params string[] modules) =>
        daemon.Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, project, modules, None);

    private static async Task PublishAsync(RunningDaemon daemon, string project, TriggerDefinition trigger)
    {
        Definition draft = await DraftAsync(daemon, project, trigger);
        await daemon.Services.GetRequiredService<DefinitionService>().PublishAsync(Principal.LocalOwner, project, draft.Id, None);
    }

    private static Task<Definition> DraftAsync(RunningDaemon daemon, string project, TriggerDefinition trigger) =>
        daemon.Services.GetRequiredService<DefinitionService>().DraftAsync(
            Principal.LocalOwner, DefinitionKind.Trigger, DefinitionScope.Project, project, trigger.Name,
            TriggerDefinition.CanonicalJson(trigger), trigger.Hash, null, None);
}
