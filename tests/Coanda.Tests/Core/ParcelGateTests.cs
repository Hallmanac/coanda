using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ParcelGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly ParcelType Gated = ShippedTypes.Task with
    {
        GateBindings = [new GateBinding("refine", "build", StandInGate.DefaultName)],
    };

    private static Parcel Create(string title, GateSet gates) =>
        Parcel.Create(ParcelDecider.Create(Gated, gates, "coanda", title, null, "brian", Now));

    [Fact]
    public void A_failing_gate_refuses_the_move_naming_the_gate_and_quoting_its_reason()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = Create("unready work", gates);

        FluentActions.Invoking(() => ParcelDecider.Move(Gated, gates, parcel, "build", "brian", Now))
            .Should().Throw<DomainException>()
            .WithMessage($"The gate 'stand-in' refused the move from 'refine' to 'build': {StandInGate.Reason}");
        parcel.State.Should().Be("refine");
        gate.Checks.Should().Equal(("refine", "build"));
    }

    [Fact]
    public void A_passing_gate_lets_the_move_through()
    {
        var gates = new GateSet([new StandInGate()]);
        Parcel parcel = Create("ready work", gates);

        ParcelMoved moved = ParcelDecider.Move(Gated, gates, parcel, "build", "brian", Now);

        moved.ToState.Should().Be("build");
    }

    [Fact]
    public void A_move_the_gate_is_not_bound_to_does_not_run_it()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = Create("unready work", gates);

        ParcelDecider.Move(Gated, gates, parcel, "paused", "brian", Now).ToState.Should().Be("paused");
        gate.Checks.Should().BeEmpty();
    }

    [Fact]
    public void A_move_out_of_paused_runs_every_gate_bound_to_a_move_into_the_target_so_pausing_bypasses_none()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = Create("unready work", gates);
        parcel.Apply(ParcelDecider.Move(Gated, gates, parcel, "paused", "brian", Now));

        FluentActions.Invoking(() => ParcelDecider.Move(Gated, gates, parcel, "build", "brian", Now))
            .Should().Throw<DomainException>()
            .WithMessage($"The gate 'stand-in' refused the move from 'paused' to 'build': {StandInGate.Reason}");
        gate.Checks.Should().Equal(("paused", "build"));
        ParcelDecider.Move(Gated, gates, parcel, "review", "brian", Now).ToState.Should().Be("review", "no gate is bound to a move into review");
    }

    [Fact]
    public void A_bound_gate_the_decider_is_not_given_refuses_the_move_naming_the_gate()
    {
        Parcel parcel = Create("ready work", new GateSet([new StandInGate()]));

        FluentActions.Invoking(() => ParcelDecider.Move(Gated, GateSet.None, parcel, "build", "brian", Now))
            .Should().Throw<DomainException>()
            .WithMessage("The gate 'stand-in' bound to the move to 'build' is not a gate this daemon runs, so the move is refused.");
    }

    [Fact]
    public void A_gate_set_refuses_two_gates_of_one_name()
    {
        FluentActions.Invoking(() => new GateSet([new StandInGate(), new StandInGate()]))
            .Should().Throw<InvalidOperationException>().WithMessage("*'stand-in' is given twice*");
    }

    private static readonly ParcelType GatedClose = ShippedTypes.Task with
    {
        GateBindings = [new GateBinding("review", ShippedTypes.Closed, StandInGate.DefaultName)],
    };

    private static Parcel InReview(ParcelType type, GateSet gates)
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(type, gates, "coanda", "unready reviewed work", null, "brian", Now));
        parcel.Apply(ParcelDecider.Move(type, gates, parcel, "build", "brian", Now));
        parcel.Apply(ParcelDecider.Move(type, gates, parcel, "review", "brian", Now));
        return parcel;
    }

    [Fact]
    public void A_gate_bound_to_the_close_refuses_the_close_naming_the_gate_and_quoting_its_reason()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = InReview(GatedClose, gates);

        FluentActions.Invoking(() => ParcelDecider.Close(GatedClose, gates, parcel, "completed", "QA has not passed", "brian", Now))
            .Should().Throw<DomainException>()
            .WithMessage($"The gate 'stand-in' refused the move from 'review' to 'closed': {StandInGate.Reason}");
        gate.Checks.Should().Equal(("review", "closed"));
    }

    [Fact]
    public void A_close_out_of_paused_runs_the_gate_bound_to_the_close_and_a_close_from_another_state_does_not()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = InReview(GatedClose, gates);
        parcel.Apply(ParcelDecider.Move(GatedClose, gates, parcel, "paused", "brian", Now));

        FluentActions.Invoking(() => ParcelDecider.Close(GatedClose, gates, parcel, "completed", "done", "brian", Now))
            .Should().Throw<DomainException>().WithMessage("The gate 'stand-in' refused*");

        Parcel fresh = Parcel.Create(ParcelDecider.Create(GatedClose, gates, "coanda", "abandoned early", null, "brian", Now));
        gate.Checks.Clear();
        ParcelDecider.Close(GatedClose, gates, fresh, "abandoned", "not needed", "brian", Now).TerminalState.Should().Be("closed");
        gate.Checks.Should().BeEmpty("the gate is bound to the close from review, not from refine");
    }

    [Fact]
    public void A_gate_bound_to_the_close_that_the_decider_is_not_given_refuses_the_close()
    {
        Parcel parcel = InReview(GatedClose, new GateSet([new StandInGate()]));

        FluentActions.Invoking(() => ParcelDecider.Close(GatedClose, GateSet.None, parcel, "completed", "done", "brian", Now))
            .Should().Throw<DomainException>()
            .WithMessage("The gate 'stand-in' bound to the move to 'closed' is not a gate this daemon runs, so the move is refused.");
    }
}
