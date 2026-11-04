using Coanda.Core.Contours;
using Coanda.Core.Software;
using Coanda.Core.Triggers;

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

        Trigger binding = Trigger.ForBinding(ShippedTypes.Task, ShippedTypes.Task.ContourBindings[0]);
        binding.Matches(entered).Should().BeTrue();
        binding.Matches(TriggerEvent.ParcelEntered("task", "review")).Should().BeFalse();
        binding.Matches(TriggerEvent.ParcelEntered("idea", "build")).Should().BeFalse();
    }

    [Fact]
    public void A_follow_matches_a_flow_run_end_with_its_outcome_and_its_contour_when_it_names_one()
    {
        TriggerEvent ended = TriggerEvent.FlowRunEnded("build", "pr-opened", "task");
        ended.Kind.Should().Be("flow-run-ended");
        ended.Fields.Should().BeEquivalentTo(new Dictionary<string, string> { ["contour"] = TriggerEvent.ContourKey("build"), ["outcome"] = "pr-opened", ["type"] = "task" });

        Trigger.ForFollow(new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 1 }).Matches(ended).Should().BeTrue();
        Trigger.ForFollow(new ContourFollow { Outcome = "pr-opened", DepthCap = 1 }).Matches(ended).Should().BeTrue("a follow naming no contour takes the outcome from any contour");
        Trigger.ForFollow(new ContourFollow { Outcome = "pr-opened", Contour = "discovery", DepthCap = 1 }).Matches(ended).Should().BeFalse();
        Trigger.ForFollow(new ContourFollow { Outcome = "report-produced", DepthCap = 1 }).Matches(ended).Should().BeFalse();
    }

    [Fact]
    public void A_follow_names_its_contour_in_any_spelling_as_contour_names_compare_without_case()
    {
        TriggerEvent ended = TriggerEvent.FlowRunEnded("build", "pr-opened", "task");

        Trigger.ForFollow(new ContourFollow { Outcome = "pr-opened", Contour = "Build", DepthCap = 1 }).Matches(ended).Should().BeTrue();
        Trigger.ForFollow(new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 1 })
            .Matches(TriggerEvent.FlowRunEnded("BUILD", "pr-opened", "task")).Should().BeTrue();
        Trigger.ForFollow(new ContourFollow { Outcome = "PR-Opened", Contour = "build", DepthCap = 1 }).Matches(ended)
            .Should().BeFalse("an outcome is a typed value and compares exactly");
    }

    [Fact]
    public void A_follow_never_matches_an_event_of_another_kind_carrying_the_same_fields()
    {
        Trigger follow = Trigger.ForFollow(new ContourFollow { Outcome = "pr-opened", DepthCap = 1 });

        follow.Matches(Event("pr-merged", ("contour", "build"), ("outcome", "pr-opened"), ("type", "task"))).Should().BeFalse();
    }
}
