using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ParcelTypeBindingTests
{
    private static readonly IReadOnlySet<string> NoGates = GateSet.None.Names;
    private static readonly IReadOnlySet<string> OneGate = new HashSet<string>(StringComparer.Ordinal) { "has-an-owner" };

    /// <summary>
    /// The shipped task without the gate bindings it ships with, so a test binds only the gates it names,
    /// and without the fields those bindings enforce, which a type may not declare unenforced.
    /// </summary>
    private static readonly ParcelType Ungated = ShippedTypes.Task with { GateBindings = [], Fields = [] };

    [Fact]
    public void The_shipped_task_binds_build_to_build_and_the_shipped_idea_binds_discovery_to_discovery_and_nothing_else()
    {
        Assert.Equal([new FlowBinding("build", "build")], ShippedTypes.Task.FlowBindings);
        Assert.Equal([new FlowBinding("discovery", "discovery")], ShippedTypes.Idea.FlowBindings);
        Assert.Empty(ShippedTypes.Idea.GateBindings);
        Assert.Equal([new GateBinding("refine", "build", RequiredFieldsGate.GateName), new GateBinding("build", "review", RequiredFieldsGate.GateName)], ShippedTypes.Task.GateBindings);
        Assert.All(ShippedTypes.All, t => Assert.True(t.FlowBindings.Count == 1, "no other stage carries a flow"));
    }

    [Fact]
    public void A_type_that_binds_nothing_hashes_to_a_literal()
    {
        Assert.Equal("3e70a726cd7c44a1", (Ungated with { FlowBindings = [] }).Hash);
        Assert.Equal("d548b43e0f79bc35", (ShippedTypes.Idea with { FlowBindings = [] }).Hash);
    }

    [Fact]
    public void Adding_or_changing_a_binding_changes_the_hash()
    {
        ParcelType unbound = Ungated with { FlowBindings = [] };
        ParcelType bound = unbound with { FlowBindings = [new FlowBinding("build", "build")] };
        ParcelType otherFlow = unbound with { FlowBindings = [new FlowBinding("build", "build-carefully")] };
        ParcelType otherStage = unbound with { FlowBindings = [new FlowBinding("review", "build")] };
        ParcelType gated = bound with { GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };
        ParcelType otherGate = bound with { GateBindings = [new GateBinding("refine", "build", "has-a-plan")] };

        string[] hashes = [unbound.Hash, bound.Hash, otherFlow.Hash, otherStage.Hash, gated.Hash, otherGate.Hash];
        Assert.Distinct(hashes);
        // the hash is a pure function of the content
        Assert.Equal(Ungated.Hash, bound.Hash);
    }

    [Fact]
    public void Bindings_survive_the_canonical_json()
    {
        ParcelType gated = Ungated with { GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };

        ParcelType read = ParcelType.FromJson(ParcelType.CanonicalJson(gated));

        Assert.Equal(gated.FlowBindings, read.FlowBindings);
        Assert.Equal(gated.GateBindings, read.GateBindings);
        Assert.Equal(gated.Hash, read.Hash);
    }

    [Fact]
    public void A_flow_binding_on_a_stage_the_type_does_not_have_is_refused()
    {
        ParcelType broken = Ungated with { FlowBindings = [new FlowBinding("shipping", "build")] };

        DomainException exception = Assert.Throws<DomainException>(() => broken.Validate(NoGates));
        Assert.Equal("Parcel type 'task' binds a flow to 'shipping', which is not one of its live stages; a flow binds to a stage the type has, never its terminal stage.", exception.Message);
    }

    [Fact]
    public void A_flow_binding_on_the_terminal_stage_is_refused()
    {
        ParcelType broken = Ungated with { FlowBindings = [new FlowBinding("closed", "build")] };

        DomainException exception = Assert.Throws<DomainException>(() => broken.Validate(NoGates));
        Assert.Matches(@"(?s)^Parcel type 'task' binds a flow to 'closed', which is not one of its live stages;.*never its terminal stage\.$", exception.Message);
    }

    [Fact]
    public void A_stage_with_more_than_one_flow_binding_is_refused()
    {
        ParcelType broken = Ungated with
        {
            FlowBindings = [new FlowBinding("build", "build"), new FlowBinding("build", "build-carefully")],
        };

        DomainException exception = Assert.Throws<DomainException>(() => broken.Validate(NoGates));
        Assert.Equal("Parcel type 'task' binds more than one flow to 'build'; a stage carries at most one flow binding.", exception.Message);
    }

    [Fact]
    public void A_gate_binding_on_a_move_the_move_table_does_not_hold_is_refused()
    {
        ParcelType broken = Ungated with { GateBindings = [new GateBinding("refine", "review", "has-an-owner")] };

        DomainException exception = Assert.Throws<DomainException>(() => broken.Validate(OneGate));
        Assert.Equal("Parcel type 'task' binds a gate to the move from 'refine' to 'review', which its move table does not hold; a gate binds only to a move the table holds or to a close from a live stage.", exception.Message);
    }

    [Fact]
    public void A_gate_binding_on_a_pause_the_table_does_not_hold_is_refused_though_the_lifecycle_allows_the_pause()
    {
        ParcelType broken = Ungated with { GateBindings = [new GateBinding("build", "paused", "has-an-owner")] };

        DomainException exception = Assert.Throws<DomainException>(() => broken.Validate(OneGate));
        Assert.Contains("which its move table does not hold", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gate_name_outside_the_set_given_is_refused()
    {
        ParcelType gated = Ungated with { GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };

        DomainException exception = Assert.Throws<DomainException>(() => gated.Validate(NoGates));
        Assert.Equal("Parcel type 'task' binds the gate 'has-an-owner', which is not a gate this daemon runs; a gate binding names a known gate.", exception.Message);
        gated.Validate(OneGate);
    }

    [Fact]
    public void A_gate_binding_on_a_close_from_a_live_stage_is_accepted_and_one_from_paused_is_refused()
    {
        ParcelType gatedClose = Ungated with { GateBindings = [new GateBinding("review", "closed", "has-an-owner")] };
        ParcelType fromPaused = Ungated with { GateBindings = [new GateBinding("paused", "closed", "has-an-owner")] };

        gatedClose.Validate(OneGate);
        DomainException exception = Assert.Throws<DomainException>(() => fromPaused.Validate(OneGate));
        Assert.Contains("which its move table does not hold", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"To": "build", "Gate": "has-an-owner"}]""", "[]")]
    [InlineData("""[{"From": "refine", "To": "build"}]""", "[]")]
    [InlineData("[null]", "[]")]
    [InlineData("[]", """[{"Flow": "build"}]""")]
    [InlineData("[]", "[null]")]
    public void A_drafted_binding_missing_a_field_or_null_is_refused_quoting_the_rule(string gateBindings, string flowBindings)
    {
        string json = ParcelType.CanonicalJson(Ungated with { FlowBindings = [] });
        Assert.Contains("\"FlowBindings\": []", json, StringComparison.Ordinal);
        Assert.Contains("\"GateBindings\": []", json, StringComparison.Ordinal);
        string content = json
            .Replace("\"FlowBindings\": []", $"\"FlowBindings\": {flowBindings}", StringComparison.Ordinal)
            .Replace("\"GateBindings\": []", $"\"GateBindings\": {gateBindings}", StringComparison.Ordinal);

        ParcelType read = ParcelType.FromJson(content);

        DomainException exception = Assert.Throws<DomainException>(() => read.Validate(OneGate));
        Assert.Matches(@"(?s)^Parcel type 'task' has a .* binding without .*$", exception.Message);
    }
}
