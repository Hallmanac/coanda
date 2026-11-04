using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A contour that declares a closing resolution closes its parcel at a flow run's end, in the same save
/// as the end and the release, or records none of the three.
/// </summary>
public sealed class CloseOnEndTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private FlowRunService flowRuns = null!;
    private DefinitionService definitions = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath, ShippedDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, None);
        parcels = new ParcelService(store, TimeProvider.System);
        flowRuns = new FlowRunService(store, TimeProvider.System);
        definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task The_end_of_a_flow_run_whose_contour_closes_its_parcel_records_the_end_the_release_and_the_close_together()
    {
        (Parcel parcel, FlowRun flowRun) = await StartedAsync(Closing("completed"));

        await flowRuns.EndAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, flowRun.Id, "closed-it", None);

        Parcel closed = await ShowAsync(parcel.Id);
        closed.Resolution.Should().Be("completed");
        closed.State.Should().Be(ShippedTypes.Closed);
        closed.LiveFlowRunId.Should().BeNull();
        IReadOnlyList<IEvent> events = await EventsAsync(parcel.Id);
        ParcelClosed recorded = events[^1].Data.Should().BeOfType<ParcelClosed>().Subject;
        events[^2].Data.Should().BeOfType<ParcelFlowRunReleased>().Which.FlowRunId.Should().Be(flowRun.Id);
        recorded.ClosedBy.Should().Be($"flow-run:{flowRun.Id}", "the flow run's own principal closes the parcel");
        recorded.Reason.Should().Be($"Flow run {Ids.Short(flowRun.Id)} of contour 'closer' reached its end, and the contour closes its parcel as 'completed'.");
        (await flowRuns.ShowAsync(Principal.LocalOwner, Project, flowRun.Id, None))?.Status.Should().Be(FlowRunStatus.Ended);
    }

    [Fact]
    public async Task A_close_the_parcels_type_refuses_records_neither_the_end_nor_the_release_nor_the_close()
    {
        (Parcel parcel, FlowRun flowRun) = await StartedAsync(Closing("split"));
        int before = (await EventsAsync(parcel.Id)).Count;

        await FluentActions.Awaiting(() => flowRuns.EndAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, flowRun.Id, "closed-it", None))
            .Should().ThrowAsync<DomainException>()
            .WithMessage($"Flow run {Ids.Short(flowRun.Id)} of contour 'closer' reached its end, but its parcel could not be closed as 'split', so neither the end nor the close was recorded: Parcel type 'task' closes with one of completed, abandoned, superseded, duplicate, not 'split'.");

        (await EventsAsync(parcel.Id)).Should().HaveCount(before);
        Parcel open = await ShowAsync(parcel.Id);
        open.Resolution.Should().BeNull();
        open.LiveFlowRunId.Should().Be(flowRun.Id);
        (await flowRuns.ShowAsync(Principal.LocalOwner, Project, flowRun.Id, None))?.Status.Should().Be(FlowRunStatus.Running);
    }

    [Fact]
    public async Task The_end_of_a_flow_run_whose_contour_declares_no_resolution_leaves_its_parcel_open()
    {
        (Parcel parcel, FlowRun flowRun) = await StartedAsync(Closing("completed") with { Name = "leaver", Closes = null });

        await flowRuns.EndAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, flowRun.Id, "closed-it", None);

        Parcel open = await ShowAsync(parcel.Id);
        open.Resolution.Should().BeNull();
        open.LiveFlowRunId.Should().BeNull();
    }

    [Fact]
    public async Task A_persons_close_of_a_parcel_a_flow_run_holds_is_still_refused_and_the_close_entry_keeps_its_roles()
    {
        (Parcel parcel, FlowRun flowRun) = await StartedAsync(Closing("completed"));

        await FluentActions.Awaiting(() => parcels.CloseAsync(Principal.LocalOwner, Project, parcel.Id, "completed", "By hand.", None))
            .Should().ThrowAsync<DomainException>().WithMessage($"*held by flow run {Ids.Short(flowRun.Id)}*");
        CommandRegistry.Entries.Single(e => e.Name == "parcel.close").AllowedRoles.Should().BeEquivalentTo([Role.Owner, Role.Member]);
        await FluentActions.Awaiting(() => parcels.CloseAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, parcel.Id, "completed", "As the flow run.", None))
            .Should().ThrowAsync<DomainException>().WithMessage("'parcel.close' is not open to the Agent role.");
    }

    private static Contour Closing(string resolution) => new()
    {
        Name = "closer",
        Accepts = ["task"],
        Outcome = "closed-it",
        Budget = 1m,
        SpawnCap = 1,
        Closes = resolution,
        Steps =
        [
            new ContourStep
            {
                Name = "wait",
                Action = CoreActions.Timer,
                Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("done"), VisitCap = 1, Target = Contour.End }],
            },
        ],
    };

    private async Task<(Parcel Parcel, FlowRun FlowRun)> StartedAsync(Contour contour)
    {
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Contour, DefinitionScope.Project, Project, contour.Name,
            Contour.CanonicalJson(contour), contour.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Close me at the end", null, None);
        FlowRun flowRun = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, contour.Name, None);
        return (parcel, flowRun);
    }

    private async Task<Parcel> ShowAsync(Guid parcelId) =>
        await parcels.ShowAsync(Principal.LocalOwner, Project, parcelId, None)
            ?? throw new InvalidOperationException($"No parcel {parcelId}.");

    private async Task<IReadOnlyList<IEvent>> EventsAsync(Guid parcelId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return await session.Events.FetchStreamAsync(parcelId, token: None);
    }
}
