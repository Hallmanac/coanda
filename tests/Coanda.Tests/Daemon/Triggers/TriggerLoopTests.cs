using Coanda.Cli;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Tests.Daemon.Flights;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Triggers;

public sealed class TriggerLoopTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A project's own type whose doing state binds a flow nobody published.</summary>
    private static readonly ParcelType Chore = new()
    {
        Name = "chore",
        States = ["todo", "doing", ShippedTypes.Parked, ShippedTypes.Closed],
        EntryStates = ["todo"],
        TerminalState = ShippedTypes.Closed,
        Resolutions = ["done"],
        ParkedState = ShippedTypes.Parked,
        Moves = new Dictionary<string, IReadOnlyList<string>> { ["todo"] = ["doing"] },
        FlowBindings = [new FlowBinding("doing", "no-such-flow")],
    };

    private static async Task PublishTypeAsync(RunningDaemon daemon, ParcelType type)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, Project, type, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
    }

    [Fact]
    public async Task A_bound_entry_whose_flight_ended_starts_no_second_flight_after_a_restart()
    {
        using var temp = new TemporaryHome();
        ParcelView parcel;
        await using (RunningDaemon first = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None))
        {
            using var client = new DaemonClient(temp.Home);
            parcel = await Flying.TaskAtBuildAsync(client, Project, "Fly once");
            FlightView bound = await Flying.BoundAsync(client, Project, parcel.Id);
            bound.StartedBy.Should().StartWith("binding:task/build/build:entry:", "a binding starts as a principal naming the binding and the entry event");
            (await Flying.UntilAsync(client, Project, bound.Id, "ended")).Outcome.Should().Be("pr-opened");
            await first.StopAsync(None);
        }

        await using RunningDaemon second = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var again = new DaemonClient(temp.Home);

        // The loop handles entries in order, so once a later entry has its flight, the earlier one was read again if it ever would be.
        ParcelView later = await Flying.TaskAtBuildAsync(again, Project, "Fly later");
        await Flying.BoundAsync(again, Project, later.Id);
        (await Flying.OfParcelAsync(again, Project, parcel.Id)).Should().ContainSingle();

        await second.StopAsync(None);
    }

    [Fact]
    public async Task An_entry_recorded_while_the_daemon_was_stopped_starts_its_flight_on_the_next_start_and_only_once()
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
            FlightView bound = await Flying.BoundAsync(client, Project, parcelId);
            bound.Flow.Should().Be("build");
            await second.StopAsync(None);
        }

        await using RunningDaemon third = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var again = new DaemonClient(temp.Home);
        ParcelView later = await Flying.TaskAtBuildAsync(again, Project, "Moved later");
        await Flying.BoundAsync(again, Project, later.Id);
        (await Flying.OfParcelAsync(again, Project, parcelId)).Should().ContainSingle();

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
        ParcelView later = await Flying.TaskAtBuildAsync(client, Project, "Moved after the install");
        await Flying.BoundAsync(client, Project, later.Id);
        (await Flying.OfParcelAsync(client, Project, parcelId)).Should().BeEmpty();

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
        ParcelView task = await Flying.TaskAtBuildAsync(client, Project, "Still builds");

        (await Flying.BoundAsync(client, Project, task.Id)).Flow.Should().Be("build");
        (await Flying.OfParcelAsync(client, Project, chore.Id)).Should().BeEmpty();

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Moving_an_idea_to_discovery_starts_the_discovery_flow()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        using var client = new DaemonClient(temp.Home);

        ParcelView idea = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "idea", "Think it through", null), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, idea.Id, "discovery"), None);

        (await Flying.BoundAsync(client, Project, idea.Id)).Flow.Should().Be("discovery");

        await daemon.StopAsync(None);
    }
}
