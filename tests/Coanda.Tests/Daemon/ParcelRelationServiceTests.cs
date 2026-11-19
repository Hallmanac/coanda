using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The parcel service creates a parcel together with a derived-from relation in one save, records a
/// blocked-by on an existing parcel, and records nothing, a parcel included, when the relation is refused.
/// Show and list return the relations the parcel's stream holds.
/// </summary>
public sealed class ParcelRelationServiceTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private const string Other = "beta";
    private static readonly Principal Person = Principal.LocalOwner;
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly TemporaryHome temp = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private FlowRunService flowRuns = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, None);
        await RegisteredProjects.AddAsync(store, Other, None);
        parcels = new ParcelService(store, TimeProvider.System);
        flowRuns = new FlowRunService(store, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task A_parcel_created_derived_from_another_holds_its_creation_then_the_relation_and_show_and_list_return_it()
    {
        Parcel source = await AddAsync("Source");
        FlowRun flowRun = await RunningOnAsync(source.Id);

        Parcel derived = await parcels.AddAsync(Person, Project, "task", "Derived", null, origin: null, new DerivedFrom(source.Id, flowRun.Id), None);

        var expected = new ParcelRelation(RelationKinds.DerivedFrom, source.Id, flowRun.Id);
        Assert.Equal([expected], derived.Relations);
        IReadOnlyList<IEvent> events = await EventsAsync(derived.Id);
        Assert.Collection(
            events,
            e => Assert.IsType<ParcelCreated>(e.Data),
            e => Assert.Equal(new ParcelRelationRecorded(derived.Id, "derived-from", source.Id, flowRun.Id, Person.Id, derived.CreatedAt), e.Data));
        Assert.Equal([expected], (await parcels.ShowAsync(Person, Project, derived.Id, None))?.Relations);
        IReadOnlyList<Parcel> listed = await parcels.ListAsync(Person, Project, None);
        Assert.Equal([expected], Assert.Single(listed, p => p.Id == derived.Id).Relations);
        Assert.Empty(Assert.Single(listed, p => p.Id == source.Id).Relations);
    }

    [Fact]
    public async Task A_derived_from_naming_no_flow_run_is_recorded_without_one()
    {
        Parcel source = await AddAsync("Source");

        Parcel derived = await parcels.AddAsync(Person, Project, "task", "Derived", null, origin: null, new DerivedFrom(source.Id), None);

        Assert.Equal([new ParcelRelation("derived-from", source.Id, null)], (await parcels.ShowAsync(Person, Project, derived.Id, None))?.Relations);
    }

    [Fact]
    public async Task A_refused_derived_from_creates_no_parcel()
    {
        Parcel source = await AddAsync("Source");
        Parcel elsewhere = await parcels.AddAsync(Person, Other, "task", "Elsewhere", null, None);
        FlowRun onSource = await RunningOnAsync(source.Id);
        Parcel sibling = await AddAsync("Sibling");

        DerivedFrom[] refused =
        [
            new(Ids.New()),
            new(elsewhere.Id),
            new(source.Id, Ids.New()),
            new(sibling.Id, onSource.Id),
        ];

        foreach (DerivedFrom derivedFrom in refused)
        {
            await Assert.ThrowsAsync<DomainException>(() => parcels.AddAsync(Person, Project, "task", "Refused", null, origin: null, derivedFrom, None));
        }

        Assert.DoesNotContain(await parcels.ListAsync(Person, Project, None), p => p.Title == "Refused");
    }

    [Fact]
    public async Task A_blocked_by_is_recorded_on_the_blocked_parcel_and_a_second_one_to_the_same_parcel_is_refused()
    {
        Parcel blocked = await AddAsync("Blocked");
        Parcel blocker = await AddAsync("Blocker");

        Parcel recorded = await parcels.RelateAsync(Person, Project, blocked.Id, "blocked-by", blocker.Id, null, None);

        Assert.Equal([new ParcelRelation("blocked-by", blocker.Id, null)], recorded.Relations);
        Assert.Equal(recorded.Relations, (await parcels.ShowAsync(Person, Project, blocked.Id, None))?.Relations);
        await RefusedUnchangedAsync(blocked.Id, () => parcels.RelateAsync(Person, Project, blocked.Id, "blocked-by", blocker.Id, null, None), "already records a blocked-by relation");
    }

    [Fact]
    public async Task A_relation_on_an_existing_parcel_is_refused_and_records_nothing_when_a_rule_refuses_it()
    {
        Parcel parcel = await AddAsync("Relating");
        Parcel other = await AddAsync("Other");
        Parcel elsewhere = await parcels.AddAsync(Person, Other, "task", "Elsewhere", null, None);
        FlowRun onOther = await RunningOnAsync(other.Id);

        await RefusedUnchangedAsync(parcel.Id, () => parcels.RelateAsync(Person, Project, parcel.Id, "member-of", other.Id, null, None), "A relation's kind is one of");
        await RefusedUnchangedAsync(parcel.Id, () => parcels.RelateAsync(Person, Project, parcel.Id, "blocked-by", parcel.Id, null, None), "to itself");
        await RefusedUnchangedAsync(parcel.Id, () => parcels.RelateAsync(Person, Project, parcel.Id, "blocked-by", elsewhere.Id, null, None), "No parcel");
        await RefusedUnchangedAsync(parcel.Id, () => parcels.RelateAsync(Person, Project, parcel.Id, "blocked-by", other.Id, onOther.Id, None), "names no flow run");
        await RefusedUnchangedAsync(parcel.Id, () => parcels.RelateAsync(Person, Project, parcel.Id, "derived-from", other.Id, null, None), "recorded only in the save that creates its parcel");

        await parcels.CloseAsync(Person, Project, parcel.Id, "abandoned", "Not wanted.", None);
        await RefusedUnchangedAsync(parcel.Id, () => parcels.RelateAsync(Person, Project, parcel.Id, "blocked-by", other.Id, null, None), "a closed parcel records no relation");
    }

    [Fact]
    public async Task Recording_a_relation_is_open_to_people_and_not_to_an_agent_or_a_module()
    {
        Parcel parcel = await AddAsync("Relating");
        Parcel other = await AddAsync("Other");

        foreach (Principal refused in new[] { new Principal("agent", Role.Agent), Principal.ForModule("software") })
        {
            await RefusedUnchangedAsync(parcel.Id, () => parcels.RelateAsync(refused, Project, parcel.Id, "blocked-by", other.Id, null, None), "'parcel.relate' is not open");
        }
    }

    private Task<Parcel> AddAsync(string title) => parcels.AddAsync(Person, Project, "task", title, null, None);

    private async Task<FlowRun> RunningOnAsync(Guid parcelId)
    {
        await parcels.BriefAsync(Person, Project, parcelId, cancellationToken: None);
        await parcels.MoveAsync(Person, Project, parcelId, "build", None);
        return await flowRuns.StartAsync(Person, Project, parcelId, "build", None);
    }

    private async Task RefusedUnchangedAsync(Guid parcelId, Func<Task<Parcel>> attempt, string refusal)
    {
        int before = (await EventsAsync(parcelId)).Count;

        DomainException exception = await Assert.ThrowsAsync<DomainException>(attempt);

        Assert.Contains(refusal, exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, (await EventsAsync(parcelId)).Count);
    }

    private async Task<IReadOnlyList<IEvent>> EventsAsync(Guid parcelId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return await session.Events.FetchStreamAsync(parcelId, token: None);
    }
}
