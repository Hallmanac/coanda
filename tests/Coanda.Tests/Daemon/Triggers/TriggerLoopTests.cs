using Coanda.Cli;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Tests.Daemon.Flows;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Triggers;

public sealed class TriggerLoopTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A project's own type whose doing state binds a contour nobody published.</summary>
    private static readonly ParcelType Chore = new()
    {
        Name = "chore",
        States = ["todo", "doing", ShippedTypes.Paused, ShippedTypes.Closed],
        EntryStates = ["todo"],
        TerminalState = ShippedTypes.Closed,
        Resolutions = ["done"],
        PausedState = ShippedTypes.Paused,
        Moves = new Dictionary<string, IReadOnlyList<string>> { ["todo"] = ["doing"] },
        ContourBindings = [new ContourBinding("doing", "no-such-contour")],
    };

    private static async Task PublishTypeAsync(RunningDaemon daemon, ParcelType type)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, Project, type, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
    }

    [Fact]
    public async Task A_bound_entry_whose_flow_ended_starts_no_second_flow_after_a_restart()
    {
        using var temp = new TemporaryHome();
        ParcelView parcel;
        await using (RunningDaemon first = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None))
        {
            using var client = new DaemonClient(temp.Home);
            parcel = await Flowing.TaskAtBuildAsync(client, Project, "Run once");
            FlowView bound = await Flowing.BoundAsync(client, Project, parcel.Id);
            bound.StartedBy.Should().StartWith("binding:task/build/build:entry:", "a binding starts as a principal naming the binding and the entry event");
            (await Flowing.UntilAsync(client, Project, bound.Id, "ended")).Outcome.Should().Be("pr-opened");
            await first.StopAsync(None);
        }

        await using RunningDaemon second = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var again = new DaemonClient(temp.Home);

        // The loop handles entries in order, so once a later entry has its flow, the earlier one was read again if it ever would be.
        ParcelView later = await Flowing.TaskAtBuildAsync(again, Project, "Run later");
        await Flowing.BoundAsync(again, Project, later.Id);
        (await Flowing.OfParcelAsync(again, Project, parcel.Id)).Should().ContainSingle();

        await second.StopAsync(None);
    }

    [Fact]
    public async Task An_entry_recorded_while_the_daemon_was_stopped_starts_its_flow_on_the_next_start_and_only_once()
    {
        using var temp = new TemporaryHome();
        await using (RunningDaemon first = await DaemonHost.StartAsync(temp.Home, 0, None))
        {
            await first.StopAsync(None);
        }

        Guid parcelId;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var parcels = new ParcelService(store, TimeProvider.System);
            Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Moved while nobody watched", null, None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            parcelId = parcel.Id;
        }

        await using (RunningDaemon second = await DaemonHost.StartAsync(temp.Home, 0, None))
        {
            using var client = new DaemonClient(temp.Home);
            FlowView bound = await Flowing.BoundAsync(client, Project, parcelId);
            bound.ContourName.Should().Be("build");
            await second.StopAsync(None);
        }

        await using RunningDaemon third = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var again = new DaemonClient(temp.Home);
        ParcelView later = await Flowing.TaskAtBuildAsync(again, Project, "Moved later");
        await Flowing.BoundAsync(again, Project, later.Id);
        (await Flowing.OfParcelAsync(again, Project, parcelId)).Should().ContainSingle();

        await third.StopAsync(None);
    }

    [Fact]
    public async Task Parcels_already_in_a_bound_state_on_a_node_where_the_loop_never_ran_start_nothing()
    {
        using var temp = new TemporaryHome();
        Guid parcelId;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinition.All))
        {
            var parcels = new ParcelService(store, TimeProvider.System);
            Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Already building", null, None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            parcelId = parcel.Id;
        }

        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView later = await Flowing.TaskAtBuildAsync(client, Project, "Moved after the install");
        await Flowing.BoundAsync(client, Project, later.Id);
        (await Flowing.OfParcelAsync(client, Project, parcelId)).Should().BeEmpty();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_refused_start_starts_nothing_and_the_loop_goes_on_to_later_entries()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var client = new DaemonClient(temp.Home);
        await PublishTypeAsync(daemon, Chore);

        ParcelView chore = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "chore", "Bound to nothing real", null), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, chore.Id, "doing"), None);
        ParcelView task = await Flowing.TaskAtBuildAsync(client, Project, "Still builds");

        (await Flowing.BoundAsync(client, Project, task.Id)).ContourName.Should().Be("build");
        (await Flowing.OfParcelAsync(client, Project, chore.Id)).Should().BeEmpty();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Moving_an_idea_to_discovery_starts_the_discovery_contour()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var client = new DaemonClient(temp.Home);

        ParcelView idea = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "idea", "Think it through", null), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, idea.Id, "discovery"), None);

        (await Flowing.BoundAsync(client, Project, idea.Id)).ContourName.Should().Be("discovery");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_entry_made_while_the_parcel_has_a_live_flow_starts_nothing_then_or_later()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var client = new DaemonClient(temp.Home);
        FlowService flows = daemon.Services.GetRequiredService<FlowService>();

        // A flow the service starts is not advanced, so it stays live on the parcel while the parcel moves,
        // the way a flow whose own step moves its parcel is still live at that move.
        ParcelView held = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Moved under a live flow", null), None);
        Flow holding = await flows.StartAsync(Principal.LocalOwner, Project, held.Id, "build", None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, held.Id, "build"), None);

        // This flow is stopped before the loop can be counted on to have read the move, so only the
        // parcel's state at the move, not at handling, refuses the entry.
        ParcelView released = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Released before the loop looked", null), None);
        Flow brief = await flows.StartAsync(Principal.LocalOwner, Project, released.Id, "build", None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, released.Id, "build"), None);
        await flows.StopAsync(Principal.LocalOwner, Project, brief.Id, "gone before the loop read the move", None);

        // The loop handles entries in order, so once a later entry has its flow, both moves were handled.
        await Flowing.BoundAsync(client, Project, (await Flowing.TaskAtBuildAsync(client, Project, "Marks the moves handled")).Id);
        (await Flowing.OfParcelAsync(client, Project, held.Id)).Should().ContainSingle().Which.Id.Should().Be(holding.Id);
        (await BoundToBuildAsync(client, released.Id)).Should().BeEmpty("the parcel had a live flow when it entered build");

        await flows.StopAsync(Principal.LocalOwner, Project, holding.Id, "nothing waits on this release", None);
        await Flowing.BoundAsync(client, Project, (await Flowing.TaskAtBuildAsync(client, Project, "Marks the release handled")).Id);
        (await Flowing.OfParcelAsync(client, Project, held.Id)).Should().ContainSingle().Which.Id.Should().Be(holding.Id);

        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, held.Id, "review"), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, held.Id, "build"), None);
        await Flowing.BoundAsync(client, Project, (await Flowing.TaskAtBuildAsync(client, Project, "Marks the re-entry handled")).Id);
        (await BoundToBuildAsync(client, held.Id)).Should().ContainSingle("an entry made with no live flow starts the bound contour once");

        await daemon.StopAsync(None);
    }

    /// <summary>The parcel's flows the build binding started, known by their principal.</summary>
    private static async Task<List<FlowView>> BoundToBuildAsync(DaemonClient client, Guid parcelId) =>
        [.. (await Flowing.OfParcelAsync(client, Project, parcelId)).Where(f => f.StartedBy.StartsWith("binding:task/build/build:entry:", StringComparison.Ordinal))];
}
