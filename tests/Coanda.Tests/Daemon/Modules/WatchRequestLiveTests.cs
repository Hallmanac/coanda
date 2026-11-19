using System.Diagnostics;
using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Coanda.Tests.Daemon.Transport;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The watch requests the daemon sends a hosted module while it runs: a project's request again whenever
/// what its triggers compile to for the module changes, never one identical to the last the module
/// accepted, and a request the module failed to accept sent again after a wait.
/// </summary>
public sealed class WatchRequestLiveTests
{
    private const string Tracker = "tracker";
    private const string Alpha = "alpha";
    private const string Beta = "beta";

    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>How long a test waits to see that nothing more is sent: ten of the loop's polls.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    private static readonly EventKindDescription[] TrackerKinds =
    [
        new("tracker/issue-opened", ["repo", "assignee"]),
        new("tracker/review-requested", ["reviewer"]),
    ];

    [Fact]
    public async Task A_project_registered_while_the_daemon_runs_gives_the_module_switched_on_by_default_its_shipped_triggers_watch()
    {
        using var temp = new TemporaryHome();
        TriggerDefinition shippedTrigger = SampleTriggers.IssueOpened;
        using StandInModuleDirectory shipped = StandInModuleDirectory.Write(Tracker, "1.0.0", ModuleDefinition.FromTrigger(shippedTrigger));
        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        await using RunningDaemon daemon = await StartAsync(temp, tracker, o => o with { ModulePaths = [shipped.Path] });
        Assert.Empty(tracker.WatchRequests);

        await RegisterAsync(daemon, Alpha);

        WatchRequest request = await LastAsync(tracker, Alpha, r => r.Watches.Count > 0);
        AssertWatch(Assert.Single(request.Watches), "tracker/issue-opened", ("assignee", "pat"), ("repo", "coanda"));
    }

    [Fact]
    public async Task A_publish_and_a_retire_each_change_the_last_request_the_module_received_for_the_project()
    {
        using var temp = new TemporaryHome();
        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        await using RunningDaemon daemon = await StartAsync(temp, tracker);
        await RegisterAsync(daemon, Alpha);
        Assert.Empty((await LastAsync(tracker, Alpha, _ => true)).Watches);

        Definition published = await PublishAsync(daemon, Alpha, SampleTriggers.ReviewRequested);
        WatchRequest afterPublish = await LastAsync(tracker, Alpha, r => r.Watches.Count == 1);
        AssertWatch(Assert.Single(afterPublish.Watches), "tracker/review-requested", ("reviewer", "pat"));

        await daemon.Services.GetRequiredService<DefinitionService>().RetireAsync(Principal.LocalOwner, Alpha, published.Id, None);
        await LastAsync(tracker, Alpha, r => r.Watches.Count == 0);
        Assert.Equal(3, tracker.WatchRequests.Count);
    }

    [Fact]
    public async Task A_module_switched_off_for_a_project_is_sent_an_empty_set_for_it()
    {
        using var temp = new TemporaryHome();
        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        await using RunningDaemon daemon = await StartAsync(temp, tracker);
        await RegisterAsync(daemon, Alpha);
        await PublishAsync(daemon, Alpha, SampleTriggers.ReviewRequested);
        await LastAsync(tracker, Alpha, r => r.Watches.Count == 1);

        await daemon.Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, Alpha, [], None);

        WatchRequest last = await LastAsync(tracker, Alpha, r => r.Watches.Count == 0);
        Assert.Equal(Alpha, last.Project);
    }

    [Fact]
    public async Task A_module_going_from_unconfigured_to_configured_by_a_setting_is_sent_the_compiled_set()
    {
        using var temp = new TemporaryHome();
        var settingsSchema = new SettingsSchema(
            [new ReplicatedSetting("channel", JsonSerializer.Deserialize<JsonElement>("""{ "type": "string", "minLength": 1 }"""), Required: true)]);
        var tracker = new FeedModule([], settingsSchema, TrackerKinds);
        await using RunningDaemon daemon = await StartAsync(temp, tracker);
        await RegisterAsync(daemon, Alpha);
        await PublishAsync(daemon, Alpha, SampleTriggers.ReviewRequested);
        await Task.Delay(Quiet, None);
        Assert.Empty(Assert.Single(tracker.WatchRequests).Watches);

        await daemon.Services.GetRequiredService<ModuleSettingsService>().SetAsync(
            Principal.LocalOwner, Alpha, Tracker, "channel", JsonSerializer.Deserialize<JsonElement>("\"builds\""), None);

        WatchRequest configured = await LastAsync(tracker, Alpha, r => r.Watches.Count == 1);
        AssertWatch(Assert.Single(configured.Watches), "tracker/review-requested", ("reviewer", "pat"));
        Assert.Equal(2, tracker.WatchRequests.Count);
    }

    [Fact]
    public async Task A_change_that_compiles_to_the_set_already_accepted_sends_nothing_and_logs_no_left_out_trigger()
    {
        using var temp = new TemporaryHome();
        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        var log = new RecordingLogger();
        await using RunningDaemon daemon = await StartAsync(temp, tracker, o => o with { LoggerProvider = log });
        await RegisterAsync(daemon, Alpha);
        await PublishAsync(daemon, Alpha, SampleTriggers.ReviewRequested);
        await LastAsync(tracker, Alpha, r => r.Watches.Count == 1);
        int before = tracker.WatchRequests.Count;

        // The same watch as one the set already holds, and a trigger left out.
        await PublishAsync(daemon, Alpha, SampleTriggers.ReviewRequested with { Name = "review-again" });
        await PublishAsync(daemon, Alpha, SampleTriggers.ReviewRequested with { Name = "by-label", Filters = SampleTriggers.Filters(("label", "urgent")) });
        await Task.Delay(Quiet, None);
        Assert.Equal(before, tracker.WatchRequests.Count);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("trigger 'by-label'", StringComparison.Ordinal));

        await PublishAsync(daemon, Alpha, SampleTriggers.IssueOpened);
        await LastAsync(tracker, Alpha, r => r.Watches.Count == 2);

        // The same watch again, its filters given in another order.
        await PublishAsync(daemon, Alpha, SampleTriggers.IssueOpened with { Name = "issue-again", Filters = SampleTriggers.Filters(("assignee", "pat"), ("repo", "coanda")) });
        await Task.Delay(Quiet, None);

        Assert.Equal(before + 1, tracker.WatchRequests.Count);
        Assert.Single(log.Entries, e => e.Message.StartsWith("Left trigger 'by-label'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_watch_call_that_throws_is_sent_again_with_nothing_changed_once_the_first_wait_has_passed()
    {
        using var temp = new TemporaryHome();
        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        var failedAt = Stopwatch.StartNew();
        int calls = 0;
        tracker.OnWatch = (_, _) =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                return Task.CompletedTask;
            }

            failedAt.Restart();
            throw new InvalidOperationException("The module cannot take its watches yet.");
        };
        var log = new RecordingLogger();
        await using RunningDaemon daemon = await StartAsync(temp, tracker, o => o with { LoggerProvider = log });

        await RegisterAsync(daemon, Alpha);

        await FlowRunDriver.UntilAsync(() => tracker.WatchRequests.Count == 2, "the failed request is sent again");
        Assert.True(failedAt.Elapsed >= ModuleWatches.FirstRetry - TimeSpan.FromMilliseconds(50), $"sent again after {failedAt.Elapsed}");
        Assert.All(tracker.WatchRequests, r => Assert.Equal(Alpha, r.Project));
        string line = Assert.Single(log.Entries, e => e.Message.StartsWith("The watch request", StringComparison.Ordinal)).Message;
        Assert.Contains($"project '{Alpha}'", line, StringComparison.Ordinal);
        Assert.Contains($"module '{Tracker}'", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_publish_in_another_project_reaches_the_module_while_its_request_for_the_first_waits_to_be_retried()
    {
        using var temp = new TemporaryHome();
        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        tracker.OnWatch = (request, _) => request.Project == Alpha
            ? throw new InvalidOperationException("The module cannot take alpha's watches.")
            : Task.CompletedTask;
        await using RunningDaemon daemon = await StartAsync(temp, tracker);
        await RegisterAsync(daemon, Alpha);
        await RegisterAsync(daemon, Beta);
        await LastAsync(tracker, Beta, _ => true);

        await PublishAsync(daemon, Beta, SampleTriggers.ReviewRequested);

        WatchRequest beta = await LastAsync(tracker, Beta, r => r.Watches.Count == 1);
        AssertWatch(Assert.Single(beta.Watches), "tracker/review-requested", ("reviewer", "pat"));
    }

    [Fact]
    public async Task A_project_scope_trigger_imported_from_another_node_changes_the_last_request_for_the_project()
    {
        await using LedgerNode peer = await LedgerNode.StartAsync(Alpha);
        await peer.RecordRootAsync(Alpha);
        await ProjectServices.Software(peer.Store, TimeProvider.System).SetModulesAsync(Principal.LocalOwner, Alpha, [Tracker], None);
        var peerDefinitions = new DefinitionService(peer.Store, TimeProvider.System);
        Definition draft = await DraftAsync(peerDefinitions, Alpha, SampleTriggers.ReviewRequested);
        await peerDefinitions.PublishAsync(Principal.LocalOwner, Alpha, draft.Id, None);
        LedgerPublication published = await peer.Export.PublishAsync(Alpha, None);
        Assert.True(published.Published, string.Join("; ", published.Failures));

        using var temp = new TemporaryHome();
        var tracker = new FeedModule([], eventKinds: TrackerKinds);
        await using RunningDaemon daemon = await StartAsync(temp, tracker, o => o with { CommandRunner = new ProcessCommandRunner() });
        Project project = await peer.ProjectAsync(Alpha);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        await using (IDocumentSession session = store.LightweightSession(Alpha))
        {
            session.Events.StartStream<Project>(project.Id, new ProjectRegistered(
                project.Id, Alpha, [ProjectRepo.FromRemote(peer.RemotePath(Alpha))], Principal.LocalOwner.Id, DateTimeOffset.UtcNow));
            session.Store(new LedgerRoot { Key = peer.Identity.Key.PublicKey });
            await session.SaveChangesAsync(None);
        }

        await daemon.Services.GetRequiredService<RepoClones>().EnsureAsync(await peer.ProjectAsync(Alpha), None);
        await daemon.Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, Alpha, [Tracker], None);
        Assert.Empty((await LastAsync(tracker, Alpha, _ => true)).Watches);

        await daemon.Services.GetRequiredService<LedgerImport>().ImportAsync(Alpha, None);

        WatchRequest imported = await LastAsync(tracker, Alpha, r => r.Watches.Count == 1);
        AssertWatch(Assert.Single(imported.Watches), "tracker/review-requested", ("reviewer", "pat"));
        await daemon.StopAsync(None);
        foreach (FileInfo file in new DirectoryInfo(temp.Home.Path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(9, 256)]
    [InlineData(10, 300)]
    [InlineData(1000, 300)]
    public void The_wait_before_a_retry_starts_at_a_second_and_doubles_to_at_most_five_minutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ModuleWatches.RetryAfter(failures));

    [Fact]
    public void Two_requests_with_the_same_watches_in_any_order_each_with_its_filters_in_any_order_are_identical()
    {
        var first = new WatchRequest(Alpha, [new Watch("tracker/a", [new("x", "1"), new("y", "2")]), new Watch("tracker/b", [])]);
        var second = new WatchRequest(Alpha, [new Watch("tracker/b", []), new Watch("tracker/a", [new("y", "2"), new("x", "1")])]);

        Assert.Equal(ModuleWatches.KeyOf(first), ModuleWatches.KeyOf(second));
        Assert.NotEqual(ModuleWatches.KeyOf(first), ModuleWatches.KeyOf(second with { Project = Beta }));
        Assert.NotEqual(ModuleWatches.KeyOf(first), ModuleWatches.KeyOf(new WatchRequest(Alpha, [new Watch("tracker/a", [new("x", "1")])])));
    }

    private static Task<RunningDaemon> StartAsync(TemporaryHome temp, FeedModule tracker, Func<DaemonOptions, DaemonOptions>? configure = null)
    {
        var options = new DaemonOptions { Modules = new Dictionary<string, IModule> { [Tracker] = tracker }, DefaultModules = [Tracker] };
        return Daemons.StartAsync(temp.Home, configure is null ? options : configure(options), None);
    }

    /// <summary>Registers the project with no repos and no settings, switched on for the node's default modules.</summary>
    private static Task<Project> RegisterAsync(RunningDaemon daemon, string project) =>
        daemon.Services.GetRequiredService<ProjectService>().AddAsync(Principal.LocalOwner, project, null, [], None);

    /// <summary>The last request the module received for the project, once it meets <paramref name="condition"/>.</summary>
    private static async Task<WatchRequest> LastAsync(FeedModule module, string project, Func<WatchRequest, bool> condition)
    {
        WatchRequest? last = null;
        await FlowRunDriver.UntilAsync(
            () => (last = module.WatchRequests.LastOrDefault(r => r.Project == project)) is { } request && condition(request),
            $"the module's last request for project '{project}' meets the test's condition");
        return last ?? throw new InvalidOperationException("No request was received.");
    }

    private static void AssertWatch(Watch watch, string kind, params (string Field, string Value)[] filters)
    {
        Assert.Equal(kind, watch.Kind);
        Assert.Equal([.. filters.Select(f => new WatchFilter(f.Field, f.Value))], watch.Filters);
    }

    private static async Task<Definition> PublishAsync(RunningDaemon daemon, string project, TriggerDefinition trigger)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await DraftAsync(definitions, project, trigger);
        return await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, None);
    }

    private static Task<Definition> DraftAsync(DefinitionService definitions, string project, TriggerDefinition trigger) =>
        definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Trigger, DefinitionScope.Project, project, trigger.Name,
            TriggerDefinition.CanonicalJson(trigger), trigger.Hash, null, None);
}
