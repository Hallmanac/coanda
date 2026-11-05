using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.SystemSteps;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Coanda.Tests.Sdk.Contract;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NJsonSchema;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The module feed with stand-in modules the daemon hosts: what it delivers, in what order, from which
/// projects, where each module's position starts and how it survives a restart, and how a failed,
/// unconfigured, or held delivery leaves the rest of the feed going.
/// </summary>
public sealed class ModuleFeedTests
{
    private const string On = "alpha";
    private const string Off = "beta";
    private const string Watcher = "watcher";
    private const string Bystander = "bystander";

    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private static readonly CoreEventKind[] AllKinds =
        [CoreEventKind.ParcelEntered, CoreEventKind.FlowRunEnded, CoreEventKind.FlowRunStopped, CoreEventKind.FlowRunPaused, CoreEventKind.ParcelFieldSet];

    [Fact]
    public async Task A_module_is_delivered_the_events_of_the_kinds_it_subscribes_to_and_no_others_in_sequence_order()
    {
        var watcher = new FeedModule([CoreEventKind.ParcelEntered, CoreEventKind.FlowRunPaused, CoreEventKind.ParcelFieldSet]);
        await using Node node = await Node.StartAsync(new() { [Watcher] = watcher });
        Parcel parcel = await node.ParcelAsync(On);
        await node.Parcels.SetFieldAsync(Principal.LocalOwner, On, parcel.Id, TaskFields.Objective, TaskFields.ObjectiveValue, None);
        FlowRun started = await node.StartFlowRunAsync(On, parcel.Id);
        await node.FlowRuns.PauseAsync(Principal.LocalOwner, On, started.Id, "Look first.", "resume", None);
        await node.FlowRuns.StopAsync(Principal.LocalOwner, On, started.Id, "Not needed.", None);
        Parcel last = await node.ParcelAsync(On, "After the stop");

        await FlowRunDriver.UntilAsync(() => watcher.Events.Count == 4, "four events are delivered");
        await Task.Delay(Quiet);

        IReadOnlyList<FeedEvent> events = watcher.Events;
        Assert.Collection(
            events,
            e => Assert.Equal(parcel.Id, Assert.IsType<FeedEvent.ParcelEntered>(e).ParcelId),
            e => Assert.Equal(TaskFields.Objective, Assert.IsType<FeedEvent.ParcelFieldSet>(e).Field),
            e => Assert.Equal(started.Id, Assert.IsType<FeedEvent.FlowRunPaused>(e).FlowRunId),
            e => Assert.Equal(last.Id, Assert.IsType<FeedEvent.ParcelEntered>(e).ParcelId));
        Assert.All(events, e => Assert.Equal(On, e.Project));
        Assert.Equal(events.Select(e => e.Sequence).Order(), events.Select(e => e.Sequence));
        Assert.All(watcher.Deliveries, d => Assert.Single(d.Events));
    }

    [Fact]
    public async Task An_event_from_a_project_the_module_is_not_switched_on_for_is_never_delivered()
    {
        var watcher = new FeedModule(AllKinds);
        await using Node node = await Node.StartAsync(new() { [Watcher] = watcher });
        Parcel elsewhere = await node.ParcelAsync(Off, "Not the module's");
        FlowRun stopped = await node.StartFlowRunAsync(Off, elsewhere.Id);
        await node.FlowRuns.StopAsync(Principal.LocalOwner, Off, stopped.Id, "Not needed.", None);
        Parcel here = await node.ParcelAsync(On, "The module's");

        await FlowRunDriver.UntilAsync(() => watcher.Events.Any(e => e is FeedEvent.ParcelEntered entered && entered.ParcelId == here.Id), "the switched-on project's event is delivered");
        await Task.Delay(Quiet);

        Assert.All(watcher.Events, e => Assert.Equal(On, e.Project));
        Assert.Single(watcher.Events);
    }

    [Fact]
    public async Task Every_delivery_of_every_kind_meets_the_feed_delivery_schema()
    {
        var watcher = new FeedModule(AllKinds);
        await using Node node = await Node.StartAsync(new() { [Watcher] = watcher });
        Parcel parcel = await node.ParcelAsync(On);
        await node.Parcels.SetFieldAsync(Principal.LocalOwner, On, parcel.Id, TaskFields.Objective, TaskFields.ObjectiveValue, None);
        FlowRun paused = await node.StartFlowRunAsync(On, parcel.Id);
        await node.FlowRuns.PauseAsync(Principal.LocalOwner, On, paused.Id, "Look first.", "resume", None);
        await node.FlowRuns.StopAsync(Principal.LocalOwner, On, paused.Id, "Not needed.", None);
        FlowRun ended = await node.StartFlowRunAsync(On, parcel.Id);
        await node.FlowRuns.EndAsync(Principal.LocalOwner, On, ended.Id, "dispatched", None);

        await FlowRunDriver.UntilAsync(() => watcher.Events.OfType<FeedEvent.FlowRunEnded>().Any(), "the end is delivered");

        Assert.Equal(
            ["FlowRunEnded", "FlowRunPaused", "FlowRunStopped", "ParcelEntered", "ParcelFieldSet"],
            watcher.Events.Select(e => e.GetType().Name).Distinct().Order(StringComparer.Ordinal));
        JsonSchema schema = ModuleSchemas.Load("feed-delivery.schema.json");
        Assert.All(watcher.Documents, document => Assert.Empty(schema.Validate(document)));
    }

    [Fact]
    public async Task A_module_whose_position_the_node_never_wrote_is_delivered_nothing_recorded_before_its_feed_first_ran()
    {
        using var temp = new TemporaryHome();
        Guid before;
        await using (Node first = await Node.StartAsync(new Dictionary<string, IModule>(), temp))
        {
            before = (await first.ParcelAsync(On, "Before the module")).Id;
            await first.SwitchOnAsync(On, Watcher);
            await first.Daemon.StopAsync(None);
        }

        var watcher = new FeedModule(AllKinds);
        await using Node second = await Node.StartAsync(new() { [Watcher] = watcher }, temp, register: false);
        Parcel after = await second.ParcelAsync(On, "After the module");

        await FlowRunDriver.UntilAsync(() => watcher.Events.Count > 0, "the new event is delivered");
        await Task.Delay(Quiet);

        FeedEvent only = Assert.Single(watcher.Events);
        Assert.Equal(after.Id, Assert.IsType<FeedEvent.ParcelEntered>(only).ParcelId);
        Assert.NotEqual(before, after.Id);
        await using IQuerySession session = second.Store.QuerySession(Coanda.Daemon.Store.Tenants.Node);
        Assert.NotNull(await session.LoadAsync<FeedPosition>(FeedPosition.IdOf(Watcher), None));
    }

    [Fact]
    public async Task On_a_feeds_first_start_the_end_of_a_live_flow_run_the_start_resumes_is_delivered()
    {
        using var temp = new TemporaryHome();
        Guid resumed;
        await using (Node first = await Node.StartAsync(new Dictionary<string, IModule>(), temp))
        {
            await first.SwitchOnAsync(On, Watcher);
            Parcel parcel = await first.ParcelAsync(On, "Live across the upgrade");

            // The test's own flow run service hands nothing to the advancer, so the flow run is live and undispatched when the daemon stops.
            resumed = (await first.StartFlowRunAsync(On, parcel.Id)).Id;
            await first.Daemon.StopAsync(None);
        }

        var watcher = new FeedModule([CoreEventKind.FlowRunEnded]);
        var step = new RunningSystemStep { Answer = (_, _) => Task.FromResult<SystemStepAnswer>(RunningSystemStep.Done()) };
        await using Node second = await Node.StartAsync(new() { [Watcher] = watcher }, temp, register: false, step: step);

        // The start resumes the flow run, which ends at once; its end lands past the feed's first position.
        await FlowRunDriver.UntilAsync(() => watcher.Events.Count > 0, "the resumed flow run's end is delivered");

        Assert.Equal(resumed, Assert.IsType<FeedEvent.FlowRunEnded>(Assert.Single(watcher.Events)).FlowRunId);
    }

    [Fact]
    public async Task A_delivery_a_daemon_stop_interrupts_before_its_position_is_written_is_delivered_again_on_the_next_start()
    {
        using var temp = new TemporaryHome();
        var interrupted = new FeedModule(AllKinds);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The module has handled the delivery and is still answering when the daemon stops.
        interrupted.OnFeed = async (_, token) =>
        {
            handled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        Guid parcelId;
        await using (Node first = await Node.StartAsync(new() { [Watcher] = interrupted }, temp))
        {
            parcelId = (await first.ParcelAsync(On)).Id;
            await handled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await first.Daemon.StopAsync(None);
        }

        var again = new FeedModule(AllKinds);
        await using Node second = await Node.StartAsync(new() { [Watcher] = again }, temp, register: false);

        await FlowRunDriver.UntilAsync(() => again.Events.Count > 0, "the interrupted delivery comes again");

        FeedEvent handledOnce = Assert.Single(interrupted.Events);
        Assert.Equal(handledOnce, again.Events[0]);
        Assert.Equal(parcelId, Assert.IsType<FeedEvent.ParcelEntered>(again.Events[0]).ParcelId);
    }

    [Fact]
    public async Task No_event_past_a_modules_position_is_skipped_across_a_restart()
    {
        using var temp = new TemporaryHome();
        var stalled = new FeedModule([CoreEventKind.ParcelEntered]);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The fourth delivery is held until the daemon stops, so the events after it wait behind it.
        stalled.OnFeed = async (_, token) =>
        {
            if (stalled.Events.Count == 4)
            {
                holding.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        List<Guid> parcels = [];
        await using (Node first = await Node.StartAsync(new() { [Watcher] = stalled }, temp))
        {
            for (int i = 0; i < 6; i++)
            {
                parcels.Add((await first.ParcelAsync(On, $"Parcel {i}")).Id);
            }

            await holding.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await first.Daemon.StopAsync(None);
        }

        var resumed = new FeedModule([CoreEventKind.ParcelEntered]);
        await using Node second = await Node.StartAsync(new() { [Watcher] = resumed }, temp, register: false);

        await FlowRunDriver.UntilAsync(() => resumed.Events.Count >= 3, "the held event and the two after it are delivered");
        await Task.Delay(Quiet);

        Assert.Equal(parcels[..3], stalled.Events.Take(3).Select(e => ((FeedEvent.ParcelEntered)e).ParcelId));
        Assert.Equal(parcels[3..], resumed.Events.Select(e => ((FeedEvent.ParcelEntered)e).ParcelId));
    }

    [Fact]
    public async Task A_delivery_the_module_fails_is_logged_and_passed_over_and_stops_neither_its_feed_nor_another_modules()
    {
        var failing = new FeedModule([CoreEventKind.ParcelEntered]);
        failing.OnFeed = (delivery, _) => failing.Deliveries.Count == 1
            ? throw new InvalidOperationException("The module could not handle it.")
            : Task.CompletedTask;
        var bystander = new FeedModule([CoreEventKind.ParcelEntered]);
        var log = new RecordingLogger();
        await using Node node = await Node.StartAsync(new() { [Watcher] = failing, [Bystander] = bystander }, log: log);
        await node.SwitchOnAsync(On, Watcher, Bystander);
        Parcel failed = await node.ParcelAsync(On, "Fails");
        Parcel next = await node.ParcelAsync(On, "Goes on");

        await FlowRunDriver.UntilAsync(() => failing.Events.Count == 2 && bystander.Events.Count == 2, "both modules are delivered both events");

        long sequence = failing.Events[0].Sequence;
        Assert.Equal(next.Id, ((FeedEvent.ParcelEntered)failing.Events[1]).ParcelId);
        Assert.Equal([failed.Id, next.Id], bystander.Events.Select(e => ((FeedEvent.ParcelEntered)e).ParcelId));
        (LogLevel level, string message) = Assert.Single(log.Entries, e => e.Message.Contains("failed and is passed over", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains($"sequence {sequence}", message, StringComparison.Ordinal);
        Assert.Contains($"project '{On}'", message, StringComparison.Ordinal);
        Assert.Contains($"module '{Watcher}'", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delivery_to_a_module_unconfigured_for_its_project_is_skipped_with_a_log_line_naming_both_and_the_feed_goes_on()
    {
        const string Configured = "gamma";
        const string Profile = "work";
        var watcher = new FeedModule([CoreEventKind.ParcelEntered], new SettingsSchema([], new CredentialProfileSetting(Required: true, DefaultsToNodeIdentity: false)));
        var log = new RecordingLogger();
        await using Node node = await Node.StartAsync(new() { [Watcher] = watcher }, log: log, projects: [On, Off, Configured]);
        await node.SwitchOnAsync(Configured, Watcher);
        Directory.CreateDirectory(Path.Combine(node.Home.ModuleAreaOf(Watcher), Profile));
        await node.Services.GetRequiredService<ModuleSettingsService>().SelectProfileAsync(Principal.LocalOwner, Configured, Watcher, Profile, None);
        Parcel skipped = await node.ParcelAsync(On, "Unconfigured here");
        Parcel delivered = await node.ParcelAsync(Configured, "Configured here");

        await FlowRunDriver.UntilAsync(() => watcher.Events.Count > 0, "the configured project's event is delivered");
        await Task.Delay(Quiet);

        FeedEvent only = Assert.Single(watcher.Events);
        Assert.Equal(delivered.Id, ((FeedEvent.ParcelEntered)only).ParcelId);
        Assert.NotEqual(skipped.Id, delivered.Id);
        string line = Assert.Single(log.Entries, e => e.Message.StartsWith("Skipped the feed delivery", StringComparison.Ordinal)).Message;
        Assert.Contains($"module '{Watcher}'", line, StringComparison.Ordinal);
        Assert.Contains($"project '{On}'", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_delivery_waiting_for_the_flow_runs_worker_delays_only_that_modules_later_deliveries()
    {
        var waiting = new FeedModule([CoreEventKind.ParcelEntered, CoreEventKind.FlowRunStopped]);
        var bystander = new FeedModule([CoreEventKind.ParcelEntered]);
        await using Node node = await Node.StartAsync(new() { [Watcher] = waiting, [Bystander] = bystander });
        await node.SwitchOnAsync(On, Watcher, Bystander);
        Parcel parcel = await node.ParcelAsync(On);
        FlowRun stopped = await node.StartFlowRunAsync(On, parcel.Id);
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Workers.Started(stopped.Id, working.Task);
        await node.FlowRuns.StopAsync(Principal.LocalOwner, On, stopped.Id, "Not needed.", None);
        Parcel later = await node.ParcelAsync(On, "After the stop");

        await FlowRunDriver.UntilAsync(() => bystander.Events.Count == 2, "the other module is delivered the later event");
        await Task.Delay(Quiet);

        // the stop and everything after it wait for the stopped flow run's worker
        Assert.Equal(parcel.Id, ((FeedEvent.ParcelEntered)Assert.Single(waiting.Events)).ParcelId);

        node.Workers.Returned(stopped.Id);
        working.SetResult();
        await FlowRunDriver.UntilAsync(() => waiting.Events.Count == 3, "the stop and the later event are delivered");

        Assert.Equal(stopped.Id, Assert.IsType<FeedEvent.FlowRunStopped>(waiting.Events[1]).FlowRunId);
        Assert.Equal(later.Id, Assert.IsType<FeedEvent.ParcelEntered>(waiting.Events[2]).ParcelId);
    }

    /// <summary>
    /// A running daemon hosting stand-in modules, with <see cref="On"/>, switched on for
    /// <see cref="Watcher"/>, and <see cref="Off"/>, which no stand-in is switched on for, registered.
    /// </summary>
    private sealed class Node : IAsyncDisposable
    {
        private readonly TemporaryHome? owned;

        private Node(RunningDaemon daemon, NodeHome home, TemporaryHome? owned)
        {
            Daemon = daemon;
            Home = home;
            this.owned = owned;
        }

        public RunningDaemon Daemon { get; }

        public NodeHome Home { get; }

        public IServiceProvider Services => Daemon.Services;

        public IDocumentStore Store => Services.GetRequiredService<IDocumentStore>();

        public ParcelService Parcels => Services.GetRequiredService<ParcelService>();

        public FlowRunWorkers Workers => Services.GetRequiredService<FlowRunWorkers>();

        /// <summary>A flow run service of the test's own, which hands nothing to the daemon's advancer.</summary>
        public FlowRunService FlowRuns => new(Store, TimeProvider.System);

        public static async Task<Node> StartAsync(
            Dictionary<string, IModule> modules,
            TemporaryHome? temp = null,
            bool register = true,
            RecordingLogger? log = null,
            string[]? projects = null,
            RunningSystemStep? step = null)
        {
            TemporaryHome? owned = temp is null ? new TemporaryHome() : null;
            NodeHome home = (temp ?? owned)!.Home;
            RunningDaemon daemon = await Daemons.StartAsync(
                home, new DaemonOptions { Modules = modules, SystemSteps = [step ?? new RunningSystemStep()], LoggerProvider = log }, None);
            var node = new Node(daemon, home, owned);
            if (register)
            {
                foreach (string project in projects ?? [On, Off])
                {
                    await RegisteredProjects.AddAsync(daemon, project, None);
                    await Publishing.FlowAsync(daemon, project, RunningSystemStep.FlowNamed());
                }

                await node.SwitchOnAsync(On, Watcher);
            }

            return node;
        }

        public Task SwitchOnAsync(string project, params string[] modules) =>
            Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, project, modules, None);

        public async Task<Parcel> ParcelAsync(string project, string title = "For the feed") =>
            await Parcels.AddAsync(Principal.LocalOwner, project, "task", title, null, None);

        public async Task<FlowRun> StartFlowRunAsync(string project, Guid parcelId) =>
            await FlowRuns.StartAsync(Principal.LocalOwner, project, parcelId, RunningSystemStep.FlowNamed().Name, None);

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAsync(None);
            await Daemon.DisposeAsync();
            owned?.Dispose();
        }
    }
}
