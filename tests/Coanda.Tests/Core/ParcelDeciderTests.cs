using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class ParcelDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_starts_at_the_types_first_entry_state_and_records_the_type_hash()
    {
        ParcelCreated created = ParcelDecider.Create(ShippedTypes.Task, GateSet.None, "coanda", "  Try it  ", null, "brian", Now);

        created.State.Should().Be("refine");
        created.TypeName.Should().Be("task");
        created.TypeHash.Should().Be(ShippedTypes.Task.Hash);
        created.Title.Should().Be("Try it");
        created.Project.Should().Be("coanda");
    }

    [Fact]
    public void Create_refuses_a_state_that_is_not_an_entry()
    {
        FluentActions.Invoking(() => ParcelDecider.Create(ShippedTypes.Task, GateSet.None, "coanda", "x", "review", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*cannot start at 'review'*");
    }

    [Fact]
    public void Move_follows_the_lifecycle_and_refuses_what_it_forbids()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.None, "coanda", "x", null, "brian", Now));

        ParcelMoved moved = ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "build", "daemon", Now);
        parcel.Apply(moved);
        parcel.State.Should().Be("build");

        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "refine", "daemon", Now))
            .Should().Throw<DomainException>().WithMessage("*does not allow a move from 'build' to 'refine'*");
    }

    [Fact]
    public void Move_to_the_terminal_state_is_refused_in_favour_of_close()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.None, "coanda", "x", null, "brian", Now));
        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "closed", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*takes a resolution*");
    }

    [Fact]
    public void Close_takes_a_known_resolution_and_a_reason_and_then_nothing_moves()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Idea, GateSet.None, "coanda", "lump of clay", null, "brian", Now));

        FluentActions.Invoking(() => ParcelDecider.Close(ShippedTypes.Idea, GateSet.None, parcel, "completed", "done", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*closes with one of split, concluded, archived*");

        ParcelClosed closed = ParcelDecider.Close(ShippedTypes.Idea, GateSet.None, parcel, "split", "Three tasks derived.", "brian", Now);
        parcel.Apply(closed);
        parcel.IsClosed.Should().BeTrue();
        parcel.State.Should().Be("closed");
        parcel.Resolution.Should().Be("split");

        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Idea, GateSet.None, parcel, "discovery", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*is closed*");
    }

    [Fact]
    public void Pause_and_resume_work_from_any_live_state()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.None, "coanda", "x", null, "brian", Now));
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "build", "daemon", Now));
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "paused", "daemon", Now));
        parcel.State.Should().Be("paused");
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "review", "brian", Now));
        parcel.State.Should().Be("review");
    }

    [Fact]
    public void A_parcel_a_live_flow_holds_is_closed_by_nobody_and_moved_only_for_that_flow()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.None, "coanda", "x", null, "brian", Now));
        Guid flow = Ids.New();
        parcel.Apply(new ParcelFlowAttached(parcel.Id, flow, Now));
        string refusal = $"Parcel {Ids.Short(parcel.Id)} is held by flow {Ids.Short(flow)}*stop flow {Ids.Short(flow)} (flow.stop) before";

        FluentActions.Invoking(() => ParcelDecider.Close(ShippedTypes.Task, GateSet.None, parcel, "split", "", $"flow:{flow}", Now))
            .Should().Throw<DomainException>().WithMessage(refusal + " closing the parcel.", "the holding flow does not close it either");
        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "closed", "brian", Now))
            .Should().Throw<DomainException>().WithMessage(refusal + " moving the parcel.", "the live-flow check runs before the terminal-state check");
        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "nowhere", "brian", Now, Ids.New()))
            .Should().Throw<DomainException>().WithMessage(refusal + " moving the parcel.", "the live-flow check runs before the move table");

        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "review", $"flow:{flow}", Now, flow))
            .Should().Throw<DomainException>().WithMessage("*does not allow a move from 'refine' to 'review'*", "the holding flow's move still follows the move table");
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "build", $"flow:{flow}", Now, flow));
        parcel.State.Should().Be("build");

        parcel.Apply(new ParcelFlowReleased(parcel.Id, flow, Now));
        parcel.Apply(ParcelDecider.Move(ShippedTypes.Task, GateSet.None, parcel, "review", "brian", Now));
        parcel.Apply(ParcelDecider.Close(ShippedTypes.Task, GateSet.None, parcel, "completed", "Done.", "brian", Now));
        parcel.IsClosed.Should().BeTrue();
    }

    [Fact]
    public void A_parcel_of_another_type_is_refused_for_that_before_its_live_flow()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(ShippedTypes.Task, GateSet.None, "coanda", "x", null, "brian", Now));
        parcel.Apply(new ParcelFlowAttached(parcel.Id, Ids.New(), Now));

        FluentActions.Invoking(() => ParcelDecider.Move(ShippedTypes.Idea, GateSet.None, parcel, "build", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*is a 'task', not a 'idea'*");
        FluentActions.Invoking(() => ParcelDecider.Close(ShippedTypes.Idea, GateSet.None, parcel, "split", "done", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*is a 'task', not a 'idea'*");
    }
}
