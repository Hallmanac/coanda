using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ParcelTypeBindingTests
{
    private static readonly IReadOnlySet<string> NoGates = GateSet.None.Names;
    private static readonly IReadOnlySet<string> OneGate = new HashSet<string>(StringComparer.Ordinal) { "has-an-owner" };

    [Fact]
    public void The_shipped_task_binds_build_to_build_and_the_shipped_idea_binds_discovery_to_discovery_and_nothing_else()
    {
        ShippedTypes.Task.FlowBindings.Should().Equal(new FlowBinding("build", "build"));
        ShippedTypes.Idea.FlowBindings.Should().Equal(new FlowBinding("discovery", "discovery"));
        ShippedTypes.All.Should().OnlyContain(t => t.GateBindings.Count == 0);
        ShippedTypes.All.Should().OnlyContain(t => t.FlowBindings.Count == 1, "no other state carries a flow");
    }

    [Fact]
    public void A_type_that_binds_nothing_keeps_the_hash_it_had_before_bindings_existed()
    {
        (ShippedTypes.Task with { FlowBindings = [] }).Hash.Should().Be("43a5a17c3896d58a");
        (ShippedTypes.Idea with { FlowBindings = [] }).Hash.Should().Be("6d2b888dfd78bbdb");
    }

    [Fact]
    public void Adding_or_changing_a_binding_changes_the_hash()
    {
        ParcelType unbound = ShippedTypes.Task with { FlowBindings = [] };
        ParcelType bound = unbound with { FlowBindings = [new FlowBinding("build", "build")] };
        ParcelType otherFlow = unbound with { FlowBindings = [new FlowBinding("build", "build-carefully")] };
        ParcelType otherState = unbound with { FlowBindings = [new FlowBinding("review", "build")] };
        ParcelType gated = bound with { GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };
        ParcelType otherGate = bound with { GateBindings = [new GateBinding("refine", "build", "has-a-plan")] };

        string[] hashes = [unbound.Hash, bound.Hash, otherFlow.Hash, otherState.Hash, gated.Hash, otherGate.Hash];
        hashes.Should().OnlyHaveUniqueItems();
        bound.Hash.Should().Be(ShippedTypes.Task.Hash, "the hash is a pure function of the content");
    }

    [Fact]
    public void Bindings_survive_the_canonical_json()
    {
        ParcelType gated = ShippedTypes.Task with { GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };

        ParcelType read = ParcelType.FromJson(ParcelType.CanonicalJson(gated));

        read.FlowBindings.Should().Equal(gated.FlowBindings);
        read.GateBindings.Should().Equal(gated.GateBindings);
        read.Hash.Should().Be(gated.Hash);
    }

    [Fact]
    public void A_flow_binding_on_a_state_the_type_does_not_have_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with { FlowBindings = [new FlowBinding("shipping", "build")] };

        FluentActions.Invoking(() => broken.Validate(NoGates)).Should().Throw<DomainException>()
            .WithMessage("Parcel type 'task' binds a flow to 'shipping', which is not one of its live states; a flow binds to a state the type has, never its terminal state.");
    }

    [Fact]
    public void A_flow_binding_on_the_terminal_state_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with { FlowBindings = [new FlowBinding("closed", "build")] };

        FluentActions.Invoking(() => broken.Validate(NoGates)).Should().Throw<DomainException>()
            .WithMessage("Parcel type 'task' binds a flow to 'closed', which is not one of its live states;*never its terminal state.");
    }

    [Fact]
    public void A_state_with_more_than_one_flow_binding_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with
        {
            FlowBindings = [new FlowBinding("build", "build"), new FlowBinding("build", "build-carefully")],
        };

        FluentActions.Invoking(() => broken.Validate(NoGates)).Should().Throw<DomainException>()
            .WithMessage("Parcel type 'task' binds more than one flow to 'build'; a state carries at most one flow binding.");
    }

    [Fact]
    public void A_gate_binding_on_a_move_the_move_table_does_not_hold_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with { GateBindings = [new GateBinding("refine", "review", "has-an-owner")] };

        FluentActions.Invoking(() => broken.Validate(OneGate)).Should().Throw<DomainException>()
            .WithMessage("Parcel type 'task' binds a gate to the move from 'refine' to 'review', which its move table does not hold; a gate binds only to a move the table holds or to a close from a live state.");
    }

    [Fact]
    public void A_gate_binding_on_a_pause_the_table_does_not_hold_is_refused_though_the_lifecycle_allows_the_pause()
    {
        ParcelType broken = ShippedTypes.Task with { GateBindings = [new GateBinding("build", "paused", "has-an-owner")] };

        FluentActions.Invoking(() => broken.Validate(OneGate)).Should().Throw<DomainException>()
            .WithMessage("*which its move table does not hold*");
    }

    [Fact]
    public void A_gate_name_outside_the_set_given_is_refused()
    {
        ParcelType gated = ShippedTypes.Task with { GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };

        FluentActions.Invoking(() => gated.Validate(NoGates)).Should().Throw<DomainException>()
            .WithMessage("Parcel type 'task' binds the gate 'has-an-owner', which is not a gate this daemon runs; a gate binding names a known gate.");
        FluentActions.Invoking(() => gated.Validate(OneGate)).Should().NotThrow();
    }

    [Fact]
    public void A_gate_binding_on_a_close_from_a_live_state_is_accepted_and_one_from_paused_is_refused()
    {
        ParcelType gatedClose = ShippedTypes.Task with { GateBindings = [new GateBinding("review", "closed", "has-an-owner")] };
        ParcelType fromPaused = ShippedTypes.Task with { GateBindings = [new GateBinding("paused", "closed", "has-an-owner")] };

        FluentActions.Invoking(() => gatedClose.Validate(OneGate)).Should().NotThrow();
        FluentActions.Invoking(() => fromPaused.Validate(OneGate)).Should().Throw<DomainException>()
            .WithMessage("*which its move table does not hold*");
    }

    [Theory]
    [InlineData("""[{"To": "build", "Gate": "has-an-owner"}]""", "[]")]
    [InlineData("""[{"From": "refine", "To": "build"}]""", "[]")]
    [InlineData("[null]", "[]")]
    [InlineData("[]", """[{"Flow": "build"}]""")]
    [InlineData("[]", "[null]")]
    public void A_drafted_binding_missing_a_field_or_null_is_refused_quoting_the_rule(string gateBindings, string flowBindings)
    {
        string json = ParcelType.CanonicalJson(ShippedTypes.Task with { FlowBindings = [] });
        json.Should().Contain("\"FlowBindings\": []").And.Contain("\"GateBindings\": []");
        string content = json
            .Replace("\"FlowBindings\": []", $"\"FlowBindings\": {flowBindings}", StringComparison.Ordinal)
            .Replace("\"GateBindings\": []", $"\"GateBindings\": {gateBindings}", StringComparison.Ordinal);

        ParcelType read = ParcelType.FromJson(content);

        FluentActions.Invoking(() => read.Validate(OneGate)).Should().Throw<DomainException>()
            .WithMessage("Parcel type 'task' has a * binding without *");
    }
}
