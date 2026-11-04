using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

/// <summary>A parcel a live flow run holds is closed by nobody and moved only on behalf of that flow run.</summary>
public sealed class LiveFlowRunHoldTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly Principal Person = Principal.LocalOwner;
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private FlowRunService flowRuns = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, None);
        parcels = new ParcelService(store, TimeProvider.System);
        flowRuns = new FlowRunService(store, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task A_persons_close_of_a_parcel_a_running_flow_run_holds_is_refused_naming_the_flow_run()
    {
        (Parcel parcel, FlowRun flowRun) = await HeldAtBuildAsync();

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.CloseAsync(Person, Project, parcel.Id, "completed", "Done by hand.", None),
            flowRun.Id,
            "closing");
    }

    [Fact]
    public async Task A_persons_move_of_a_parcel_a_running_flow_run_holds_is_refused_naming_the_flow_run()
    {
        (Parcel parcel, FlowRun flowRun) = await HeldAtBuildAsync();

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.MoveAsync(Person, Project, parcel.Id, "review", None),
            flowRun.Id,
            "moving");
    }

    [Fact]
    public async Task A_close_of_a_parcel_a_paused_flow_run_holds_is_refused()
    {
        (Parcel parcel, FlowRun flowRun) = await HeldAtBuildAsync();
        (await flowRuns.PauseAsync(Person, Project, flowRun.Id, "Waiting on a person.", "Answer it.", None)).Status.Should().Be(FlowRunStatus.Paused);

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.CloseAsync(Person, Project, parcel.Id, "completed", "Done by hand.", None),
            flowRun.Id,
            "closing");
    }

    [Fact]
    public async Task A_close_of_a_held_parcel_with_a_resolution_its_type_lacks_still_gets_the_live_flow_run_refusal()
    {
        (Parcel parcel, FlowRun flowRun) = await HeldAtBuildAsync();

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.CloseAsync(Person, Project, parcel.Id, "split", "   ", None),
            flowRun.Id,
            "closing");
    }

    [Fact]
    public async Task A_move_made_on_behalf_of_a_different_flow_run_is_refused()
    {
        (Parcel parcel, FlowRun flowRun) = await HeldAtBuildAsync();
        Guid other = Ids.New();

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.MoveAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, parcel.Id, "review", other, None),
            flowRun.Id,
            "moving");
    }

    [Fact]
    public async Task The_holding_flow_runs_own_move_parcel_step_moves_the_parcel()
    {
        (Parcel parcel, FlowRun flowRun) = await HeldAtBuildAsync();
        FlowStep step = ShippedFlows.Build.Step("move-to-review")
            ?? throw new InvalidOperationException("The build flow has no move-to-review step.");
        var action = new MoveParcelAction(parcels);

        ActionOutcome outcome = await action.PerformAsync(
            new ActionRequest(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, flowRun.Id, parcel.Id, ShippedFlows.Build, step, Resumed: false),
            None);

        outcome.Should().BeOfType<ActionOutcome.Done>();
        Parcel? moved = await parcels.ShowAsync(Person, Project, parcel.Id, None);
        moved.Should().NotBeNull();
        moved.State.Should().Be("review");
        moved.LiveFlowRunId.Should().Be(flowRun.Id);
    }

    [Fact]
    public async Task Once_the_flow_run_is_stopped_a_persons_close_of_the_parcel_succeeds()
    {
        (Parcel parcel, FlowRun flowRun) = await HeldAtBuildAsync();
        await flowRuns.StopAsync(Person, Project, flowRun.Id, "Not wanted any more.", None);

        Parcel closed = await parcels.CloseAsync(Person, Project, parcel.Id, "abandoned", "Not wanted any more.", None);

        closed.IsClosed.Should().BeTrue();
        closed.Resolution.Should().Be("abandoned");
    }

    /// <summary>A task moved to build by a person, then held by a build flow run that is started and not advanced.</summary>
    private async Task<(Parcel Parcel, FlowRun FlowRun)> HeldAtBuildAsync()
    {
        Parcel parcel = await parcels.AddAsync(Person, Project, "task", "Held by a flow run", null, None);
        await parcels.BriefAsync(Person, Project, parcel.Id, cancellationToken: None);
        await parcels.MoveAsync(Person, Project, parcel.Id, "build", None);
        FlowRun flowRun = await flowRuns.StartAsync(Person, Project, parcel.Id, "build", None);
        Parcel held = await parcels.ShowAsync(Person, Project, parcel.Id, None)
            ?? throw new InvalidOperationException("The parcel just added is missing.");
        held.LiveFlowRunId.Should().Be(flowRun.Id);
        return (held, flowRun);
    }

    private async Task RefusedUnchangedAsync(Guid parcelId, Func<Task<Parcel>> attempt, Guid holder, string doing)
    {
        Parcel? before = await parcels.ShowAsync(Person, Project, parcelId, None);
        IReadOnlyList<object> eventsBefore = await EventsAsync(parcelId);

        await FluentActions.Awaiting(attempt).Should().ThrowAsync<DomainException>()
            .WithMessage($"*held by flow run {Ids.Short(holder)}*stop flow run {Ids.Short(holder)} (flow-run.stop) before {doing} the parcel*");

        Parcel? after = await parcels.ShowAsync(Person, Project, parcelId, None);
        after.Should().BeEquivalentTo(before);
        (await EventsAsync(parcelId)).Should().HaveCount(eventsBefore.Count, "a refusal appends nothing to the parcel's stream");
    }

    private async Task<IReadOnlyList<object>> EventsAsync(Guid parcelId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
