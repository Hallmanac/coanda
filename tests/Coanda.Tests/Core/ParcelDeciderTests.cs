using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class ParcelDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_at_the_types_first_entry_stage_and_records_the_type_hash()
    {
        ParcelCreated created = ParcelDecider.Create(ShippedTypes.Task, GateSet.Shipped, "coanda", "  Try it  ", null, "brian", Now);

        created.Stage.Should().Be("refine");
        created.TypeName.Should().Be("task");
        created.TypeHash.Should().Be(ShippedTypes.Task.Hash);
        created.Title.Should().Be("Try it");
        created.Project.Should().Be("coanda");
    }

    [Fact]
    public void Create_refuses_a_stage_that_is_not_an_entry()
    {
        FluentActions.Invoking(() => ParcelDecider.Create(ShippedTypes.Task, GateSet.Shipped, "coanda", "x", "review", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*cannot start at 'review'*");
    }

    [Fact]
    public void Move_follows_the_lifecycle_and_refuses_what_it_forbids()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now)).Briefed(ShippedTypes.Task);

        ParcelMoved moved = ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "build", "daemon", Now);
        parcel.Apply(moved);
        parcel.Stage.Should().Be("build");

        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "refine", "daemon", Now))
            .Should().Throw<DomainException>().WithMessage("*does not allow a move from 'build' to 'refine'*");
    }

    [Fact]
    public void Move_to_the_terminal_stage_is_refused_in_favour_of_close()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now));
        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "closed", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*takes a resolution*");
    }

    [Fact]
    public void Close_takes_a_known_resolution_and_a_reason_and_then_nothing_moves()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Idea, GateSet.Shipped, "coanda", "lump of clay", null, "brian", Now));

        FluentActions.Invoking(() => ParcelDecider.Close(ShippedTypes.Idea, GateSet.Shipped, parcel, "completed", "done", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*closes with one of split, concluded, archived*");

        ParcelClosed closed = ParcelDecider.Close(ShippedTypes.Idea, GateSet.Shipped, parcel, "split", "Three tasks derived.", "brian", Now);
        parcel.Apply(closed);
        parcel.IsClosed.Should().BeTrue();
        parcel.Stage.Should().Be("closed");
        parcel.Resolution.Should().Be("split");

        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Idea, GateSet.Shipped, parcel, "discovery", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*is closed*");
    }

    [Fact]
    public void Pause_and_resume_work_from_any_live_stage()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now)).Briefed(ShippedTypes.Task);
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "build", "daemon", Now));
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "paused", "daemon", Now));
        parcel.Stage.Should().Be("paused");
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "review", "brian", Now));
        parcel.Stage.Should().Be("review");
    }

    [Fact]
    public void A_parcel_a_live_flow_run_holds_is_closed_by_nobody_and_moved_only_for_that_flow_run()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now)).Briefed(ShippedTypes.Task);
        Guid flowRun = Ids.New();
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, flowRun, Now));
        string refusal = $"Parcel {Ids.Short(parcel.Id)} is held by flow run {Ids.Short(flowRun)}*stop flow run {Ids.Short(flowRun)} (flow-run.stop) before";

        FluentActions.Invoking(() => ParcelDecider.Close(ShippedTypes.Task, GateSet.Shipped, parcel, "split", "", $"flow-run:{flowRun}", Now))
            .Should().Throw<DomainException>().WithMessage(refusal + " closing the parcel.", "the holding flow run does not close it either");
        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "closed", "brian", Now))
            .Should().Throw<DomainException>().WithMessage(refusal + " moving the parcel.", "the live-flow-run check runs before the terminal-stage check");
        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "nowhere", "brian", Now, Ids.New()))
            .Should().Throw<DomainException>().WithMessage(refusal + " moving the parcel.", "the live-flow-run check runs before the move table");

        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "review", $"flow-run:{flowRun}", Now, flowRun))
            .Should().Throw<DomainException>().WithMessage("*does not allow a move from 'refine' to 'review'*", "the holding flow run's move still follows the move table");
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "build", $"flow-run:{flowRun}", Now, flowRun));
        parcel.Stage.Should().Be("build");

        parcel.Apply(new ParcelFlowRunReleased(parcel.Id, flowRun, Now));
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.Shipped, parcel, "review", "brian", Now));
        parcel.Apply(ParcelDecider.Close(ShippedTypes.Task, GateSet.Shipped, parcel, "completed", "Done.", "brian", Now));
        parcel.IsClosed.Should().BeTrue();
    }

    [Fact]
    public void A_parcel_of_another_type_is_refused_for_that_before_its_live_flow_run()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now));
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, Ids.New(), Now));

        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Idea, GateSet.Shipped, parcel, "build", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*is a 'task', not a 'idea'*");
        FluentActions.Invoking(() => ParcelDecider.Close(ShippedTypes.Idea, GateSet.Shipped, parcel, "split", "done", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*is a 'task', not a 'idea'*");
    }
}
