using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A flow that declares a closing resolution closes its parcel at a flow run's end, in the same save
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
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, None);
        parcels = new ParcelService(store, TimeProvider.System);
        flowRuns = new FlowRunService(store, TimeProvider.System);
        definitions = new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task The_end_of_a_flow_run_whose_flow_closes_its_parcel_records_the_end_the_release_and_the_close_together()
    {
        (Parcel parcel, FlowRun flowRun) = await StartedAsync(Closing("completed"));

        await flowRuns.EndAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, flowRun.Id, "closed-it", None);

        Parcel closed = await ShowAsync(parcel.Id);
        Assert.Equal("completed", closed.Resolution);
        Assert.Equal(SoftwareTypes.Closed, closed.Stage);
        Assert.Null(closed.LiveFlowRunId);
        IReadOnlyList<IEvent> events = await EventsAsync(parcel.Id);
        ParcelClosed recorded = Assert.IsType<ParcelClosed>(events[^1].Data);
        Assert.Equal(flowRun.Id, Assert.IsType<ParcelFlowRunReleased>(events[^2].Data).FlowRunId);
        // the flow run's own principal closes the parcel
        Assert.Equal($"flow-run:{flowRun.Id}", recorded.ClosedBy);
        Assert.Equal($"Flow run {Ids.Short(flowRun.Id)} of flow 'closer' reached its end, and the flow closes its parcel as 'completed'.", recorded.Reason);
        Assert.Equal(FlowRunStatus.Ended, (await flowRuns.ShowAsync(Principal.LocalOwner, Project, flowRun.Id, None))?.Status);
    }

    [Fact]
    public async Task A_close_the_parcels_type_refuses_records_neither_the_end_nor_the_release_nor_the_close()
    {
        (Parcel parcel, FlowRun flowRun) = await StartedAsync(Closing("split"));
        int before = (await EventsAsync(parcel.Id)).Count;

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => flowRuns.EndAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, flowRun.Id, "closed-it", None));
        Assert.Equal($"Flow run {Ids.Short(flowRun.Id)} of flow 'closer' reached its end, but its parcel could not be closed as 'split', so neither the end nor the close was recorded: Parcel type 'task' closes with one of completed, abandoned, superseded, duplicate, not 'split'.", exception.Message);

        Assert.Equal(before, (await EventsAsync(parcel.Id)).Count);
        Parcel open = await ShowAsync(parcel.Id);
        Assert.Null(open.Resolution);
        Assert.Equal(flowRun.Id, open.LiveFlowRunId);
        Assert.Equal(FlowRunStatus.Running, (await flowRuns.ShowAsync(Principal.LocalOwner, Project, flowRun.Id, None))?.Status);
    }

    [Fact]
    public async Task The_end_of_a_flow_run_whose_flow_declares_no_resolution_leaves_its_parcel_open()
    {
        (Parcel parcel, FlowRun flowRun) = await StartedAsync(Closing("completed") with { Name = "leaver", Closes = null });

        await flowRuns.EndAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, flowRun.Id, "closed-it", None);

        Parcel open = await ShowAsync(parcel.Id);
        Assert.Null(open.Resolution);
        Assert.Null(open.LiveFlowRunId);
    }

    [Fact]
    public async Task A_persons_close_of_a_parcel_a_flow_run_holds_is_still_refused_and_the_close_entry_keeps_its_roles()
    {
        (Parcel parcel, FlowRun flowRun) = await StartedAsync(Closing("completed"));

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => parcels.CloseAsync(Principal.LocalOwner, Project, parcel.Id, "completed", "By hand.", None));
        Assert.Contains($"held by flow run {Ids.Short(flowRun.Id)}", exception.Message, StringComparison.Ordinal);
        Assert.Equivalent(new[] { Role.Owner, Role.Member }, CommandRegistry.Entries.Single(e => e.Name == "parcel.close").AllowedRoles, strict: true);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => parcels.CloseAsync(FlowRunAdvancer.PrincipalFor(flowRun.Id), Project, parcel.Id, "completed", "As the flow run.", None));
        Assert.Equal("'parcel.close' is not open to the Agent role.", exception2.Message);
    }

    private static Flow Closing(string resolution) => new()
    {
        Name = "closer",
        Accepts = ["task"],
        Outcome = "closed-it",
        Budget = 1m,
        SpawnCap = 1,
        Closes = resolution,
        Steps =
        [
            new FlowStep
            {
                Name = "wait",
                SystemStep = CoreSystemSteps.Timer,
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    private async Task<(Parcel Parcel, FlowRun FlowRun)> StartedAsync(Flow flow)
    {
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, flow.Name,
            Flow.CanonicalJson(flow), flow.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Close me at the end", null, None);
        FlowRun flowRun = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, flow.Name, None);
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
