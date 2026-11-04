using Coanda.Cli;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Triggers;

public sealed class TriggerLoopTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A project's own type whose doing stage binds a flow nobody published.</summary>
    private static readonly ParcelType Chore = new()
    {
        Name = "chore",
        Stages = ["todo", "doing", ShippedTypes.Paused, ShippedTypes.Closed],
        EntryStages = ["todo"],
        TerminalStage = ShippedTypes.Closed,
        Resolutions = ["done"],
        PausedStage = ShippedTypes.Paused,
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
    public async Task A_bound_entry_whose_flow_run_ended_starts_no_second_flow_run_after_a_restart()
    {
        using var temp = new TemporaryHome();
        ParcelView parcel;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            using var client = new DaemonClient(temp.Home);
            parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Run once");
            FlowRunView bound = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
            // a binding starts as a principal naming the binding and the entry event
            Assert.StartsWith("binding:task/build/build:entry:", bound.StartedBy, StringComparison.Ordinal);
            Assert.Equal("pr-opened", (await FlowRunDriver.UntilAsync(client, Project, bound.Id, "ended")).Outcome);
            await FlowRunDriver.MergedAsync(client, Project, parcel.Id, bound.Id);
            await first.StopAsync(None);
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, None);
        using var again = new DaemonClient(temp.Home);

        // The loop handles entries in order, so once a later entry has its flow run, the earlier one was read again if it ever would be.
        ParcelView later = await FlowRunDriver.TaskAtBuildAsync(again, Project, "Run later");
        await FlowRunDriver.BoundAsync(again, Project, later.Id);
        // the bound entry started one flow run, and the merge followed its end
        Assert.Equal(["build", "merge"], (await FlowRunDriver.OfParcelAsync(again, Project, parcel.Id)).Select(f => f.FlowName));

        await second.StopAsync(None);
    }

    [Fact]
    public async Task An_entry_recorded_while_the_daemon_was_stopped_starts_its_flow_run_on_the_next_start_and_only_once()
    {
        using var temp = new TemporaryHome();
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            await first.StopAsync(None);
        }

        Guid parcelId;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            var parcels = new ParcelService(store, TimeProvider.System);
            Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Moved while nobody watched", null, None);
            await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            parcelId = parcel.Id;
        }

        await using (RunningDaemon second = await Daemons.StartAsync(temp.Home, None))
        {
            using var client = new DaemonClient(temp.Home);
            FlowRunView bound = await FlowRunDriver.BoundAsync(client, Project, parcelId);
            Assert.Equal("build", bound.FlowName);
            await second.StopAsync(None);
        }

        await using RunningDaemon third = await Daemons.StartAsync(temp.Home, None);
        using var again = new DaemonClient(temp.Home);
        ParcelView later = await FlowRunDriver.TaskAtBuildAsync(again, Project, "Moved later");
        await FlowRunDriver.BoundAsync(again, Project, later.Id);
        Assert.Single(await FlowRunDriver.OfParcelAsync(again, Project, parcelId));

        await third.StopAsync(None);
    }

    [Fact]
    public async Task Parcels_already_in_a_bound_stage_on_a_node_where_the_loop_never_ran_start_nothing()
    {
        using var temp = new TemporaryHome();
        Guid parcelId;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            var parcels = new ParcelService(store, TimeProvider.System);
            Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Already building", null, None);
            await parcels.BriefAsync(Principal.LocalOwner, Project, parcel.Id, cancellationToken: None);
            await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None);
            parcelId = parcel.Id;
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView later = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Moved after the install");
        await FlowRunDriver.BoundAsync(client, Project, later.Id);
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, parcelId));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_refused_start_starts_nothing_and_the_loop_goes_on_to_later_entries()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        await PublishTypeAsync(daemon, Chore);

        ParcelView chore = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "chore", "Bound to nothing real", null), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, chore.Id, "doing"), None);
        ParcelView task = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Still builds");

        Assert.Equal("build", (await FlowRunDriver.BoundAsync(client, Project, task.Id)).FlowName);
        Assert.Empty(await FlowRunDriver.OfParcelAsync(client, Project, chore.Id));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Moving_an_idea_to_discovery_starts_the_discovery_flow()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);

        ParcelView idea = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "idea", "Think it through", null), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, idea.Id, "discovery"), None);

        Assert.Equal("discovery", (await FlowRunDriver.BoundAsync(client, Project, idea.Id)).FlowName);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_entry_made_while_the_parcel_has_a_live_flow_run_starts_nothing_then_or_later()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        FlowRunService flowRuns = daemon.Services.GetRequiredService<FlowRunService>();
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();

        // A flow run the service starts is not advanced, so it stays live on the parcel while the parcel moves,
        // the way a flow run whose own step moves its parcel is still live at that move. Each move is made on
        // behalf of the flow run holding the parcel, as that flow run's move-parcel step would make it.
        ParcelView held = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Moved under a live flow run", null), None);
        await client.BriefAsync(Project, held.Id, cancellationToken: None);
        FlowRun holding = await flowRuns.StartAsync(Principal.LocalOwner, Project, held.Id, "build", None);
        await parcels.MoveAsync(FlowRunAdvancer.PrincipalFor(holding.Id), Project, held.Id, "build", holding.Id, None);

        // This flow run is stopped before the loop can be counted on to have read the move, so only the
        // parcel's stage at the move, not at handling, refuses the entry.
        ParcelView released = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Released before the loop looked", null), None);
        await client.BriefAsync(Project, released.Id, cancellationToken: None);
        FlowRun brief = await flowRuns.StartAsync(Principal.LocalOwner, Project, released.Id, "build", None);
        await parcels.MoveAsync(FlowRunAdvancer.PrincipalFor(brief.Id), Project, released.Id, "build", brief.Id, None);
        await flowRuns.StopAsync(Principal.LocalOwner, Project, brief.Id, "gone before the loop read the move", None);

        // The loop handles entries in order, so once a later entry has its flow run, both moves were handled.
        await FlowRunDriver.BoundAsync(client, Project, (await FlowRunDriver.TaskAtBuildAsync(client, Project, "Marks the moves handled")).Id);
        Assert.Equal(holding.Id, Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, held.Id)).Id);
        // the parcel had a live flow run when it entered build
        Assert.Empty(await BoundToBuildAsync(client, released.Id));

        await flowRuns.StopAsync(Principal.LocalOwner, Project, holding.Id, "nothing waits on this release", None);
        await FlowRunDriver.BoundAsync(client, Project, (await FlowRunDriver.TaskAtBuildAsync(client, Project, "Marks the release handled")).Id);
        Assert.Equal(holding.Id, Assert.Single(await FlowRunDriver.OfParcelAsync(client, Project, held.Id)).Id);

        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, held.Id, "review"), None);
        await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, held.Id, "build"), None);
        await FlowRunDriver.BoundAsync(client, Project, (await FlowRunDriver.TaskAtBuildAsync(client, Project, "Marks the re-entry handled")).Id);
        // an entry made with no live flow run starts the bound flow once
        Assert.Single(await BoundToBuildAsync(client, held.Id));

        await daemon.StopAsync(None);
    }

    /// <summary>The parcel's flow runs the build binding started, known by their principal.</summary>
    private static async Task<List<FlowRunView>> BoundToBuildAsync(DaemonClient client, Guid parcelId) =>
        [.. (await FlowRunDriver.OfParcelAsync(client, Project, parcelId)).Where(f => f.StartedBy.StartsWith("binding:task/build/build:entry:", StringComparison.Ordinal))];
}
