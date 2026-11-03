using Coanda.Core.Triggers;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class TriggerTests
{
    private static readonly Trigger Assigned = new("pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian", ["repo"] = "coanda" });

    private static TriggerEvent Event(string kind, params (string Name, string Value)[] fields) =>
        new(kind, fields.ToDictionary(f => f.Name, f => f.Value));

    [Fact]
    public void A_trigger_matches_an_event_of_its_kind_whose_fields_equal_every_filter()
    {
        Assigned.Matches(Event("pr-assigned", ("assignee", "brian"), ("repo", "coanda"), ("title", "Fix it"))).Should().BeTrue();
    }

    [Fact]
    public void A_trigger_does_not_match_when_one_filter_value_differs()
    {
        Assigned.Matches(Event("pr-assigned", ("assignee", "someone-else"), ("repo", "coanda"))).Should().BeFalse();
        Assigned.Matches(Event("pr-assigned", ("assignee", "Brian"), ("repo", "coanda"))).Should().BeFalse("equality is exact");
    }

    [Fact]
    public void A_trigger_does_not_match_when_the_event_lacks_a_filtered_field()
    {
        Assigned.Matches(Event("pr-assigned", ("assignee", "brian"))).Should().BeFalse();
    }

    [Fact]
    public void A_trigger_does_not_match_an_event_of_another_kind()
    {
        Assigned.Matches(Event("pr-opened", ("assignee", "brian"), ("repo", "coanda"))).Should().BeFalse();
    }

    [Fact]
    public void A_parcel_entry_reads_as_parcel_entered_with_its_type_and_state_and_a_binding_is_that_trigger()
    {
        TriggerEvent entered = TriggerEvent.ParcelEntered("task", "build");
        entered.Kind.Should().Be("parcel-entered");
        entered.Fields.Should().BeEquivalentTo(new Dictionary<string, string> { ["type"] = "task", ["state"] = "build" });

        Trigger binding = Trigger.ForBinding(ShippedTypes.Task, ShippedTypes.Task.FlowBindings[0]);
        binding.Matches(entered).Should().BeTrue();
        binding.Matches(TriggerEvent.ParcelEntered("task", "review")).Should().BeFalse();
        binding.Matches(TriggerEvent.ParcelEntered("idea", "build")).Should().BeFalse();
    }
}
