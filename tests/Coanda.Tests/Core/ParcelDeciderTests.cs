using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;

namespace Coanda.Tests.Core;

public sealed class ParcelDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_at_the_types_first_entry_stage_and_records_the_type_hash()
    {
        ParcelCreated created = ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "  Try it  ", null, "brian", Now);

        Assert.Equal("refine", created.Stage);
        Assert.Equal("task", created.TypeName);
        Assert.Equal(SoftwareTypes.Task.Hash, created.TypeHash);
        Assert.Equal("Try it", created.Title);
        Assert.Equal("coanda", created.Project);
    }

    [Fact]
    public void Create_refuses_a_stage_that_is_not_an_entry()
    {
        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "x", "review", "brian", Now));
        Assert.Contains("cannot start at 'review'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Move_follows_the_lifecycle_and_refuses_what_it_forbids()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now)).Briefed(SoftwareTypes.Task);

        ParcelMoved moved = ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "build", "daemon", Now);
        parcel.Apply(moved);
        Assert.Equal("build", parcel.Stage);

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "refine", "daemon", Now));
        Assert.Contains("does not allow a move from 'build' to 'refine'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Move_to_the_terminal_stage_is_refused_in_favour_of_close()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now));
        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "closed", "brian", Now));
        Assert.Contains("takes a resolution", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_takes_a_known_resolution_and_a_reason_and_then_nothing_moves()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Idea, GateSet.Shipped, "coanda", "lump of clay", null, "brian", Now));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Close(SoftwareTypes.Idea, GateSet.Shipped, parcel, "completed", "done", "brian", Now));
        Assert.Contains("closes with one of split, concluded, archived", exception.Message, StringComparison.Ordinal);

        ParcelClosed closed = ParcelDecider.Close(SoftwareTypes.Idea, GateSet.Shipped, parcel, "split", "Three tasks derived.", "brian", Now);
        parcel.Apply(closed);
        Assert.True(parcel.IsClosed);
        Assert.Equal("closed", parcel.Stage);
        Assert.Equal("split", parcel.Resolution);

        DomainException exception2 = Assert.Throws<DomainException>(() => ParcelDecider.Move(SoftwareTypes.Idea, GateSet.Shipped, parcel, "discovery", "brian", Now));
        Assert.Contains("is closed", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pause_and_resume_work_from_any_live_stage()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now)).Briefed(SoftwareTypes.Task);
        parcel.Apply(ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "build", "daemon", Now));
        parcel.Apply(ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "paused", "daemon", Now));
        Assert.Equal("paused", parcel.Stage);
        parcel.Apply(ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "review", "brian", Now));
        Assert.Equal("review", parcel.Stage);
    }

    [Fact]
    public void A_parcel_a_live_flow_run_holds_is_closed_by_nobody_and_moved_only_for_that_flow_run()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now)).Briefed(SoftwareTypes.Task);
        Guid flowRun = Ids.New();
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, flowRun, Now));
        string refusal = $"Parcel {Ids.Short(parcel.Id)} is held by flow run {Ids.Short(flowRun)}*stop flow run {Ids.Short(flowRun)} (flow-run.stop) before";

        DomainException closing = Assert.Throws<DomainException>(() => ParcelDecider.Close(SoftwareTypes.Task, GateSet.Shipped, parcel, "split", "", $"flow-run:{flowRun}", Now));
        // the holding flow run does not close it either
        Assert.Matches(WildcardPattern.ToRegex(refusal + " closing the parcel."), closing.Message);
        DomainException toClosed = Assert.Throws<DomainException>(() => ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "closed", "brian", Now));
        // the live-flow-run check runs before the terminal-stage check
        Assert.Matches(WildcardPattern.ToRegex(refusal + " moving the parcel."), toClosed.Message);
        DomainException toNowhere = Assert.Throws<DomainException>(() => ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "nowhere", "brian", Now, Ids.New()));
        // the live-flow-run check runs before the move table
        Assert.Matches(WildcardPattern.ToRegex(refusal + " moving the parcel."), toNowhere.Message);

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "review", $"flow-run:{flowRun}", Now, flowRun));
        // the holding flow run's move still follows the move table
        Assert.Contains("does not allow a move from 'refine' to 'review'", exception.Message, StringComparison.Ordinal);
        parcel.Apply(ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "build", $"flow-run:{flowRun}", Now, flowRun));
        Assert.Equal("build", parcel.Stage);

        parcel.Apply(new ParcelFlowRunReleased(parcel.Id, flowRun, Now));
        parcel.Apply(ParcelDecider.Move(SoftwareTypes.Task, GateSet.Shipped, parcel, "review", "brian", Now));
        parcel.Apply(ParcelDecider.Close(SoftwareTypes.Task, GateSet.Shipped, parcel, "completed", "Done.", "brian", Now));
        Assert.True(parcel.IsClosed);
    }

    [Fact]
    public void A_parcel_of_another_type_is_refused_for_that_before_its_live_flow_run()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now));
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, Ids.New(), Now));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(SoftwareTypes.Idea, GateSet.Shipped, parcel, "build", "brian", Now));
        Assert.Contains("is a 'task', not a 'idea'", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(() => ParcelDecider.Close(SoftwareTypes.Idea, GateSet.Shipped, parcel, "split", "done", "brian", Now));
        Assert.Contains("is a 'task', not a 'idea'", exception2.Message, StringComparison.Ordinal);
    }
}
