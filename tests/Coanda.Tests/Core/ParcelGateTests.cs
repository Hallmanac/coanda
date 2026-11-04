using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ParcelGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The shipped task with a stand-in gate in place of its fields and the gate that enforces them.</summary>
    private static readonly ParcelType Gated = ShippedTypes.Task with
    {
        Fields = [],
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

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(Gated, gates, parcel, "build", "brian", Now));
        Assert.Equal($"The gate 'stand-in' refused the move from 'refine' to 'build': {StandInGate.Reason}", exception.Message);
        Assert.Equal("refine", parcel.Stage);
        Assert.Equal([("refine", "build")], gate.Checks);
    }

    [Fact]
    public void A_passing_gate_lets_the_move_through()
    {
        var gates = new GateSet([new StandInGate()]);
        Parcel parcel = Create("ready work", gates);

        ParcelMoved moved = ParcelDecider.Move(Gated, gates, parcel, "build", "brian", Now);

        Assert.Equal("build", moved.ToStage);
    }

    [Fact]
    public void A_move_the_gate_is_not_bound_to_does_not_run_it()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = Create("unready work", gates);

        Assert.Equal("paused", ParcelDecider.Move(Gated, gates, parcel, "paused", "brian", Now).ToStage);
        Assert.Empty(gate.Checks);
    }

    [Fact]
    public void A_move_out_of_paused_runs_every_gate_bound_to_a_move_into_the_target_so_pausing_bypasses_none()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = Create("unready work", gates);
        parcel.Apply(ParcelDecider.Move(Gated, gates, parcel, "paused", "brian", Now));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(Gated, gates, parcel, "build", "brian", Now));
        Assert.Equal($"The gate 'stand-in' refused the move from 'paused' to 'build': {StandInGate.Reason}", exception.Message);
        // the gate judges the move its binding names, not the resume
        Assert.Equal([("refine", "build")], gate.Checks);
        // no gate is bound to a move into review
        Assert.Equal("review", ParcelDecider.Move(Gated, gates, parcel, "review", "brian", Now).ToStage);
    }

    [Fact]
    public void A_bound_gate_the_decider_is_not_given_refuses_the_move_naming_the_gate()
    {
        Parcel parcel = Create("ready work", new GateSet([new StandInGate()]));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(Gated, GateSet.None, parcel, "build", "brian", Now));
        Assert.Equal("The gate 'stand-in' bound to the move to 'build' is not a gate this daemon runs, so the move is refused.", exception.Message);
    }

    [Fact]
    public void A_gate_set_refuses_two_gates_of_one_name()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => new GateSet([new StandInGate(), new StandInGate()]));
        Assert.Contains("'stand-in' is given twice", exception.Message, StringComparison.Ordinal);
    }

    private static readonly ParcelType GatedClose = ShippedTypes.Task with
    {
        Fields = [],
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

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Close(GatedClose, gates, parcel, "completed", "QA has not passed", "brian", Now));
        Assert.Equal($"The gate 'stand-in' refused the move from 'review' to 'closed': {StandInGate.Reason}", exception.Message);
        Assert.Equal([("review", "closed")], gate.Checks);
    }

    [Fact]
    public void A_close_out_of_paused_runs_the_gate_bound_to_the_close_and_a_close_from_another_stage_does_not()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = InReview(GatedClose, gates);
        parcel.Apply(ParcelDecider.Move(GatedClose, gates, parcel, "paused", "brian", Now));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Close(GatedClose, gates, parcel, "completed", "done", "brian", Now));
        Assert.StartsWith("The gate 'stand-in' refused", exception.Message, StringComparison.Ordinal);

        Parcel fresh = Parcel.Create(ParcelDecider.Create(GatedClose, gates, "coanda", "abandoned early", null, "brian", Now));
        gate.Checks.Clear();
        Assert.Equal("closed", ParcelDecider.Close(GatedClose, gates, fresh, "abandoned", "not needed", "brian", Now).TerminalStage);
        // the gate is bound to the close from review, not from refine
        Assert.Empty(gate.Checks);
    }

    [Fact]
    public void A_gate_bound_to_the_close_that_the_decider_is_not_given_refuses_the_close()
    {
        Parcel parcel = InReview(GatedClose, new GateSet([new StandInGate()]));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Close(GatedClose, GateSet.None, parcel, "completed", "done", "brian", Now));
        Assert.Equal("The gate 'stand-in' bound to the move to 'closed' is not a gate this daemon runs, so the move is refused.", exception.Message);
    }

    [Fact]
    public void A_move_or_close_of_a_parcel_a_live_flow_run_holds_is_refused_before_any_gate_runs()
    {
        var gate = new StandInGate();
        var gates = new GateSet([gate]);
        Parcel parcel = Create("ready work", gates);
        Guid flowRun = Ids.New();
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, flowRun, Now));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(Gated, gates, parcel, "build", "brian", Now));
        Assert.Contains($"held by flow run {Ids.Short(flowRun)}", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(() => ParcelDecider.Move(Gated, gates, parcel, "build", $"flow-run:{flowRun}", Now, Ids.New()));
        Assert.Contains($"held by flow run {Ids.Short(flowRun)}", exception2.Message, StringComparison.Ordinal);
        DomainException exception3 = Assert.Throws<DomainException>(() => ParcelDecider.Close(Gated, gates, parcel, "completed", "done", "brian", Now));
        Assert.Contains($"held by flow run {Ids.Short(flowRun)}", exception3.Message, StringComparison.Ordinal);
        // the live-flow-run refusal comes before any gate
        Assert.Empty(gate.Checks);

        Assert.Equal("build", ParcelDecider.Move(Gated, gates, parcel, "build", $"flow-run:{flowRun}", Now, flowRun).ToStage);
        Assert.Equal([("refine", "build")], gate.Checks);
    }
}
