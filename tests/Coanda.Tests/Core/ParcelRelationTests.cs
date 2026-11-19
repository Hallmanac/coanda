using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;

namespace Coanda.Tests.Core;

/// <summary>
/// The rules a relation is recorded under, and the fold: a parcel lists every relation its stream holds, in
/// stream order, an imported one the rules would have refused included.
/// </summary>
public sealed class ParcelRelationTests
{
    private const string Project = "alpha";
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_blocked_by_to_another_parcel_of_the_project_is_recorded_with_its_kind_as_text()
    {
        Parcel blocked = New();
        Parcel blocker = New();

        ParcelRelationRecorded recorded = ParcelDecider.Relate(blocked, "blocked-by", blocker.Id, blocker, null, null, "owner", Now);

        Assert.Equal(new ParcelRelationRecorded(blocked.Id, "blocked-by", blocker.Id, null, "owner", Now), recorded);
        blocked.Apply(recorded);
        Assert.Equal([new ParcelRelation("blocked-by", blocker.Id, null)], blocked.Relations);
        Assert.Equal(Now, blocked.UpdatedAt);
    }

    [Fact]
    public void A_derived_from_at_creation_names_the_flow_run_of_the_parcel_derived_from()
    {
        Parcel source = New();
        FlowRun flowRun = FlowRunOn(source.Id);
        ParcelCreated created = Created();

        ParcelRelationRecorded recorded = ParcelDecider.DeriveFrom(created, new DerivedFrom(source.Id, flowRun.Id), source, flowRun, Now);

        Assert.Equal(new ParcelRelationRecorded(created.ParcelId, "derived-from", source.Id, flowRun.Id, "owner", Now), recorded);
    }

    [Theory]
    [InlineData("stacked-on", "A relation's kind is one of derived-from, blocked-by, not 'stacked-on'.")]
    [InlineData("Blocked-By", "A relation's kind is one of derived-from, blocked-by, not 'Blocked-By'.")]
    [InlineData(null, "A relation's kind is one of derived-from, blocked-by, not ''.")]
    public void A_kind_that_is_not_one_of_the_two_is_refused(string? kind, string refusal)
    {
        Parcel parcel = New();
        Parcel other = New();

        Assert.Equal(refusal, Refused(() => ParcelDecider.Relate(parcel, kind, other.Id, other, null, null, "owner", Now)));
    }

    [Fact]
    public void A_relation_to_the_parcel_itself_is_refused()
    {
        Parcel parcel = New();

        Assert.Equal(
            $"Parcel {Ids.Short(parcel.Id)} cannot record a blocked-by relation to itself; a relation names another parcel.",
            Refused(() => ParcelDecider.Relate(parcel, "blocked-by", parcel.Id, parcel, null, null, "owner", Now)));

        ParcelCreated created = Created();
        Assert.Equal(
            $"Parcel {Ids.Short(created.ParcelId)} cannot record a derived-from relation to itself; a relation names another parcel.",
            Refused(() => ParcelDecider.DeriveFrom(created, new DerivedFrom(created.ParcelId), Parcel.Create(created), null, Now)));
    }

    [Fact]
    public void A_relation_to_a_parcel_the_node_does_not_hold_or_of_another_project_is_refused()
    {
        Parcel parcel = New();
        Parcel elsewhere = Parcel.Create(Created("beta"));
        string refusal(Guid id) => $"No parcel {Ids.Short(id)} in project 'alpha' on this node; a relation names a parcel of the same project that this node holds.";

        Guid missing = Ids.New();
        Assert.Equal(refusal(missing), Refused(() => ParcelDecider.Relate(parcel, "blocked-by", missing, null, null, null, "owner", Now)));
        Assert.Equal(refusal(elsewhere.Id), Refused(() => ParcelDecider.Relate(parcel, "blocked-by", elsewhere.Id, elsewhere, null, null, "owner", Now)));
        Assert.Equal(refusal(missing), Refused(() => ParcelDecider.DeriveFrom(Created(), new DerivedFrom(missing), null, null, Now)));
    }

    [Fact]
    public void A_closed_parcel_records_no_relation()
    {
        Parcel parcel = New();
        Parcel other = New();
        parcel.Apply(new ParcelClosed(parcel.Id, "refine", "closed", "abandoned", "Not wanted.", "owner", Now));

        Assert.Equal(
            $"Parcel {Ids.Short(parcel.Id)} is closed (abandoned); a closed parcel records no relation.",
            Refused(() => ParcelDecider.Relate(parcel, "blocked-by", other.Id, other, null, null, "owner", Now)));
    }

    [Fact]
    public void A_second_relation_of_the_same_kind_to_the_same_parcel_is_refused_and_one_of_another_parcel_is_not()
    {
        Parcel parcel = New();
        Parcel other = New();
        Parcel third = New();
        parcel.Apply(ParcelDecider.Relate(parcel, "blocked-by", other.Id, other, null, null, "owner", Now));

        Assert.Equal(
            $"Parcel {Ids.Short(parcel.Id)} already records a blocked-by relation to parcel {Ids.Short(other.Id)}.",
            Refused(() => ParcelDecider.Relate(parcel, "blocked-by", other.Id, other, null, null, "owner", Now)));
        parcel.Apply(ParcelDecider.Relate(parcel, "blocked-by", third.Id, third, null, null, "owner", Now));
        Assert.Equal([other.Id, third.Id], parcel.Relations.Select(r => r.RelatedParcelId));
    }

    [Fact]
    public void A_derived_from_on_an_existing_parcel_is_refused()
    {
        Parcel parcel = New();
        Parcel other = New();

        Assert.Equal(
            $"Parcel {Ids.Short(parcel.Id)} already exists, and a derived-from relation is recorded only in the save that creates its parcel.",
            Refused(() => ParcelDecider.Relate(parcel, "derived-from", other.Id, other, null, null, "owner", Now)));
    }

    [Fact]
    public void A_blocked_by_that_names_a_flow_run_is_refused()
    {
        Parcel parcel = New();
        Parcel other = New();
        FlowRun flowRun = FlowRunOn(other.Id);

        Assert.Equal(
            $"A blocked-by relation names no flow run, and this one names flow run {Ids.Short(flowRun.Id)}; only a derived-from relation names the flow run that caused it.",
            Refused(() => ParcelDecider.Relate(parcel, "blocked-by", other.Id, other, flowRun.Id, flowRun, "owner", Now)));
    }

    [Fact]
    public void A_derived_from_naming_a_flow_run_the_node_does_not_hold_or_of_another_parcel_is_refused()
    {
        Parcel source = New();
        Parcel elsewhere = New();
        FlowRun onElsewhere = FlowRunOn(elsewhere.Id);
        Guid missing = Ids.New();

        Assert.Equal(
            $"No flow run {Ids.Short(missing)} in project 'alpha' on this node; a derived-from relation names a flow run this node holds.",
            Refused(() => ParcelDecider.DeriveFrom(Created(), new DerivedFrom(source.Id, missing), source, null, Now)));
        Assert.Equal(
            $"Flow run {Ids.Short(onElsewhere.Id)} runs on parcel {Ids.Short(elsewhere.Id)}, not parcel {Ids.Short(source.Id)}; a derived-from relation names a flow run of the parcel derived from.",
            Refused(() => ParcelDecider.DeriveFrom(Created(), new DerivedFrom(source.Id, onElsewhere.Id), source, onElsewhere, Now)));
    }

    [Fact]
    public void The_fold_lists_every_relation_in_stream_order_even_one_the_rules_would_refuse()
    {
        Parcel parcel = New();
        Guid unheld = Ids.New();
        Guid flowRun = Ids.New();
        ParcelRelationRecorded[] imported =
        [
            new(parcel.Id, "derived-from", unheld, flowRun, "peer", Now),
            new(parcel.Id, "blocked-by", unheld, null, "peer", Now),
            new(parcel.Id, "blocked-by", unheld, null, "peer", Now.AddMinutes(1)),
            new(parcel.Id, "stacked-on", unheld, null, "peer", Now.AddMinutes(2)),
        ];

        foreach (ParcelRelationRecorded relation in imported)
        {
            parcel.Apply(relation);
        }

        Assert.Equal(
            [
                new ParcelRelation("derived-from", unheld, flowRun),
                new ParcelRelation("blocked-by", unheld, null),
                new ParcelRelation("blocked-by", unheld, null),
                new ParcelRelation("stacked-on", unheld, null),
            ],
            parcel.Relations);
    }

    private static string Refused(Func<ParcelRelationRecorded> attempt) => Assert.Throws<DomainException>(attempt).Message;

    private static ParcelCreated Created(string project = Project) =>
        new(Ids.New(), project, "task", SoftwareTypes.Task.Hash, "refine", "A parcel", "owner", Now);

    private static Parcel New() => Parcel.Create(Created());

    private static FlowRun FlowRunOn(Guid parcelId) =>
        FlowRun.Create(new FlowRunStarted(Ids.New(), parcelId, "build", 1, "hash", "owner", "work", StepKind.AgentStep, "builder", Now));
}
