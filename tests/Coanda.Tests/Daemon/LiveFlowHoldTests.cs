using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

/// <summary>A parcel a live flow holds is closed by nobody and moved only on behalf of that flow.</summary>
public sealed class LiveFlowHoldTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly Principal Person = Principal.LocalOwner;
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private FlowService flows = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinition.All);
        parcels = new ParcelService(store, TimeProvider.System);
        flows = new FlowService(store, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task A_persons_close_of_a_parcel_a_running_flow_holds_is_refused_naming_the_flow()
    {
        (Parcel parcel, Flow flow) = await HeldAtBuildAsync();

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.CloseAsync(Person, Project, parcel.Id, "completed", "Done by hand.", None),
            flow.Id,
            "closing");
    }

    [Fact]
    public async Task A_persons_move_of_a_parcel_a_running_flow_holds_is_refused_naming_the_flow()
    {
        (Parcel parcel, Flow flow) = await HeldAtBuildAsync();

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.MoveAsync(Person, Project, parcel.Id, "review", None),
            flow.Id,
            "moving");
    }

    [Fact]
    public async Task A_close_of_a_parcel_a_paused_flow_holds_is_refused()
    {
        (Parcel parcel, Flow flow) = await HeldAtBuildAsync();
        (await flows.PauseAsync(Person, Project, flow.Id, "Waiting on a person.", "Answer it.", None)).Status.Should().Be(FlowStatus.Paused);

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.CloseAsync(Person, Project, parcel.Id, "completed", "Done by hand.", None),
            flow.Id,
            "closing");
    }

    [Fact]
    public async Task A_close_of_a_held_parcel_with_a_resolution_its_type_lacks_still_gets_the_live_flow_refusal()
    {
        (Parcel parcel, Flow flow) = await HeldAtBuildAsync();

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.CloseAsync(Person, Project, parcel.Id, "split", "   ", None),
            flow.Id,
            "closing");
    }

    [Fact]
    public async Task A_move_made_on_behalf_of_a_different_flow_is_refused()
    {
        (Parcel parcel, Flow flow) = await HeldAtBuildAsync();
        Guid other = Ids.New();

        await RefusedUnchangedAsync(
            parcel.Id,
            () => parcels.MoveAsync(FlowRunner.PrincipalFor(flow.Id), Project, parcel.Id, "review", other, None),
            flow.Id,
            "moving");
    }

    [Fact]
    public async Task The_holding_flows_own_move_parcel_step_moves_the_parcel()
    {
        (Parcel parcel, Flow flow) = await HeldAtBuildAsync();
        ContourStep step = ShippedContours.Build.Step("move-to-review")
            ?? throw new InvalidOperationException("The build contour has no move-to-review step.");
        var action = new MoveParcelAction(parcels);

        ActionOutcome outcome = await action.PerformAsync(
            new ActionRequest(FlowRunner.PrincipalFor(flow.Id), Project, flow.Id, parcel.Id, ShippedContours.Build, step, Resumed: false),
            None);

        outcome.Should().BeOfType<ActionOutcome.Done>();
        Parcel? moved = await parcels.ShowAsync(Person, Project, parcel.Id, None);
        moved.Should().NotBeNull();
        moved.State.Should().Be("review");
        moved.LiveFlowId.Should().Be(flow.Id);
    }

    [Fact]
    public async Task Once_the_flow_is_stopped_a_persons_close_of_the_parcel_succeeds()
    {
        (Parcel parcel, Flow flow) = await HeldAtBuildAsync();
        await flows.StopAsync(Person, Project, flow.Id, "Not wanted any more.", None);

        Parcel closed = await parcels.CloseAsync(Person, Project, parcel.Id, "abandoned", "Not wanted any more.", None);

        closed.IsClosed.Should().BeTrue();
        closed.Resolution.Should().Be("abandoned");
    }

    /// <summary>A task moved to build by a person, then held by a build flow that is started and not advanced.</summary>
    private async Task<(Parcel Parcel, Flow Flow)> HeldAtBuildAsync()
    {
        Parcel parcel = await parcels.AddAsync(Person, Project, "task", "Held by a flow", null, None);
        await parcels.MoveAsync(Person, Project, parcel.Id, "build", None);
        Flow flow = await flows.StartAsync(Person, Project, parcel.Id, "build", None);
        Parcel held = await parcels.ShowAsync(Person, Project, parcel.Id, None)
            ?? throw new InvalidOperationException("The parcel just added is missing.");
        held.LiveFlowId.Should().Be(flow.Id);
        return (held, flow);
    }

    private async Task RefusedUnchangedAsync(Guid parcelId, Func<Task<Parcel>> attempt, Guid holder, string doing)
    {
        Parcel? before = await parcels.ShowAsync(Person, Project, parcelId, None);
        IReadOnlyList<object> eventsBefore = await EventsAsync(parcelId);

        await FluentActions.Awaiting(attempt).Should().ThrowAsync<DomainException>()
            .WithMessage($"*held by flow {Ids.Short(holder)}*stop flow {Ids.Short(holder)} (flow.stop) before {doing} the parcel*");

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
