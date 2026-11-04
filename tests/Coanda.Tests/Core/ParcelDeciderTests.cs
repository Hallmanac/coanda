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
}
