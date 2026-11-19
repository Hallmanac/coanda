using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Triggers;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The node configuration's <c>watchedProjects</c>: a node hands its modules the watches of the projects
/// the list names and an empty set for every other project, and refuses a module's external event in a
/// project it does not watch; a node whose configuration gives no list watches every project.
/// </summary>
public sealed class WatchedProjectsTests
{
    private const string Tracker = "tracker";
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private const string TrackerKind = "tracker/review-requested";

    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly EventKindDescription[] TrackerKinds = [new(TrackerKind, ["reviewer"])];

    private static readonly Dictionary<string, string> Fields = new() { ["reviewer"] = "pat" };

    [Theory]
    [InlineData("""{ "executor": "scripted", "watchedProjects": ["alpha"] }""")]
    [InlineData("""{ "executor": "scripted", "watchedProjects": [" ALPHA ", "not-joined-yet"] }""")]
    public async Task A_node_that_watches_one_of_two_projects_sends_the_compiled_watch_for_it_and_an_empty_set_for_the_other(string configuration)
    {
        using var temp = new TemporaryHome();
        await PrepareAsync(temp);
        File.WriteAllText(temp.Home.ConfigurationPath, configuration);
        var tracker = new FeedModule([], eventKinds: TrackerKinds);

        await using RunningDaemon daemon = await StartAsync(temp, tracker);

        Assert.Equal([Alpha, Beta], tracker.WatchRequests.Select(r => r.Project));
        Watch watch = Assert.Single(tracker.WatchRequests[0].Watches);
        Assert.Equal(TrackerKind, watch.Kind);
        Assert.Equal([new WatchFilter("reviewer", "pat")], watch.Filters);
        Assert.Empty(tracker.WatchRequests[1].Watches);

        // A trigger published while the daemon runs is sent for the watched project, and never for the other.
        await PublishAsync(daemon, Alpha, SampleTriggers.ReviewRequested with { Name = "later", Filters = SampleTriggers.Filters(("reviewer", "sam")) });
        await PublishAsync(daemon, Beta, SampleTriggers.ReviewRequested with { Name = "later", Filters = SampleTriggers.Filters(("reviewer", "sam")) });
        await FlowRunDriver.UntilAsync(
            () => tracker.WatchRequests.Any(r => r.Project == Alpha && r.Watches.Count == 2), "the watched project's request carries the later trigger");
        Assert.All(tracker.WatchRequests.Where(r => r.Project == Beta), r => Assert.Empty(r.Watches));
        await daemon.StopAsync(None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "executor": "scripted" }""")]
    public async Task A_node_whose_configuration_gives_no_watched_projects_sends_every_project_its_compiled_watches(string? configuration)
    {
        using var temp = new TemporaryHome();
        await PrepareAsync(temp);
        if (configuration is not null)
        {
            File.WriteAllText(temp.Home.ConfigurationPath, configuration);
        }

        var tracker = new FeedModule([], eventKinds: TrackerKinds);

        await using RunningDaemon daemon = await StartAsync(temp, tracker);

        Assert.Equal([Alpha, Beta], tracker.WatchRequests.Select(r => r.Project));
        Assert.All(tracker.WatchRequests, r => Assert.Equal(TrackerKind, Assert.Single(r.Watches).Kind));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_node_that_watches_no_project_sends_every_module_an_empty_set_for_every_project()
    {
        using var temp = new TemporaryHome();
        await PrepareAsync(temp);
        File.WriteAllText(temp.Home.ConfigurationPath, """{ "executor": "scripted", "watchedProjects": [] }""");
        var tracker = new FeedModule([], eventKinds: TrackerKinds);

        await using RunningDaemon daemon = await StartAsync(temp, tracker);

        Assert.Equal([Alpha, Beta], tracker.WatchRequests.Select(r => r.Project));
        Assert.All(tracker.WatchRequests, r => Assert.Empty(r.Watches));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_module_is_refused_an_event_in_a_project_the_node_does_not_watch_while_an_owner_and_a_watched_project_are_not()
    {
        using var temp = new TemporaryHome();
        await PrepareAsync(temp);
        File.WriteAllText(temp.Home.ConfigurationPath, """{ "executor": "scripted", "watchedProjects": ["beta"] }""");
        await using RunningDaemon daemon = await StartAsync(temp, new FeedModule([], eventKinds: TrackerKinds));
        TriggerService triggers = daemon.Services.GetRequiredService<TriggerService>();
        Principal module = Principal.ForModule(Tracker);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => triggers.RecordEventAsync(module, Alpha, TrackerKind, "delivery-1", null, Fields, None));
        ExternalEvent byOwner = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, "pr-assigned", "delivery-1", null, Fields, None);
        ExternalEvent watched = await triggers.RecordEventAsync(module, Beta, TrackerKind, "delivery-1", null, Fields, None);

        Assert.Equal(
            "This node does not watch project 'alpha', so module 'tracker' cannot record an external event in it here; \"watchedProjects\" in the node configuration decides which projects a node watches.",
            refused.Message);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        Assert.Equal([byOwner.Id], (await StoredAsync(store, Alpha)).Select(e => e.Id));
        Assert.Equal([watched.Id], (await StoredAsync(store, Beta)).Select(e => e.Id));
        Assert.Equal(TrackerKind, watched.Kind);
        await daemon.StopAsync(None);
    }

    /// <summary>
    /// Registers <see cref="Alpha"/> and <see cref="Beta"/>, switches the stand-in module on for both, and
    /// publishes a trigger on its event kind in each, on a daemon that is then stopped.
    /// </summary>
    private static async Task PrepareAsync(TemporaryHome temp)
    {
        await using RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions(), None);
        foreach (string project in new[] { Alpha, Beta })
        {
            await RegisteredProjects.AddAsync(first, project, None);
            await first.Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, project, [Tracker], None);
            await PublishAsync(first, project, SampleTriggers.ReviewRequested);
        }

        await first.StopAsync(None);
    }

    /// <summary>Starts the daemon as <c>coa daemon run</c> does, from the node configuration, hosting the stand-in module.</summary>
    private static Task<RunningDaemon> StartAsync(TemporaryHome temp, FeedModule tracker) =>
        Daemons.StartAsync(
            temp.Home,
            NodeConfiguration.Load(temp.Home) with { Modules = new Dictionary<string, IModule> { [Tracker] = tracker } },
            None);

    private static async Task PublishAsync(RunningDaemon daemon, string project, TriggerDefinition trigger)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Trigger, DefinitionScope.Project, project, trigger.Name,
            TriggerDefinition.CanonicalJson(trigger), trigger.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, None);
    }

    private static async Task<IReadOnlyList<ExternalEvent>> StoredAsync(IDocumentStore store, string project)
    {
        await using IQuerySession session = store.QuerySession(project);
        return await session.Query<ExternalEvent>().ToListAsync(None);
    }
}
