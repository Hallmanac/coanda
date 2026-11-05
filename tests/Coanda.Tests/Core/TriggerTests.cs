using Coanda.Core.Flows;
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
        Assert.True(Assigned.Matches(Event("pr-assigned", ("assignee", "brian"), ("repo", "coanda"), ("title", "Fix it"))));
    }

    [Fact]
    public void A_trigger_does_not_match_when_one_filter_value_differs()
    {
        Assert.False(Assigned.Matches(Event("pr-assigned", ("assignee", "someone-else"), ("repo", "coanda"))));
        Assert.False(Assigned.Matches(Event("pr-assigned", ("assignee", "Brian"), ("repo", "coanda"))), "equality is exact");
    }

    [Fact]
    public void A_trigger_does_not_match_when_the_event_lacks_a_filtered_field()
    {
        Assert.False(Assigned.Matches(Event("pr-assigned", ("assignee", "brian"))));
    }

    [Fact]
    public void A_trigger_does_not_match_an_event_of_another_kind()
    {
        Assert.False(Assigned.Matches(Event("pr-opened", ("assignee", "brian"), ("repo", "coanda"))));
    }

    [Fact]
    public void A_parcel_entry_reads_as_parcel_entered_with_its_type_and_stage_and_a_binding_is_that_trigger()
    {
        TriggerEvent entered = TriggerEvent.ParcelEntered("task", "build");
        Assert.Equal("parcel-entered", entered.Kind);
        Assert.Equivalent(new Dictionary<string, string> { ["type"] = "task", ["stage"] = "build" }, entered.Fields, strict: true);

        Trigger binding = Trigger.ForBinding(SoftwareTypes.Task, SoftwareTypes.Task.FlowBindings[0]);
        Assert.True(binding.Matches(entered));
        Assert.False(binding.Matches(TriggerEvent.ParcelEntered("task", "review")));
        Assert.False(binding.Matches(TriggerEvent.ParcelEntered("idea", "build")));
    }

    [Fact]
    public void A_follow_matches_a_flow_run_end_with_its_outcome_and_its_flow_when_it_names_one()
    {
        TriggerEvent ended = TriggerEvent.FlowRunEnded("build", "pr-opened", "task");
        Assert.Equal("flow-run-ended", ended.Kind);
        Assert.Equivalent(new Dictionary<string, string> { ["flow"] = TriggerEvent.FlowKey("build"), ["outcome"] = "pr-opened", ["type"] = "task" }, ended.Fields, strict: true);

        Assert.True(Trigger.ForFollow(new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 }).Matches(ended));
        Assert.True(Trigger.ForFollow(new FlowFollow { Outcome = "pr-opened", DepthCap = 1 }).Matches(ended), "a follow naming no flow takes the outcome from any flow");
        Assert.False(Trigger.ForFollow(new FlowFollow { Outcome = "pr-opened", Flow = "discovery", DepthCap = 1 }).Matches(ended));
        Assert.False(Trigger.ForFollow(new FlowFollow { Outcome = "report-produced", DepthCap = 1 }).Matches(ended));
    }

    [Fact]
    public void A_follow_names_its_flow_in_any_spelling_as_flow_names_compare_without_case()
    {
        TriggerEvent ended = TriggerEvent.FlowRunEnded("build", "pr-opened", "task");

        Assert.True(Trigger.ForFollow(new FlowFollow { Outcome = "pr-opened", Flow = "Build", DepthCap = 1 }).Matches(ended));
        Assert.True(Trigger.ForFollow(new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 })
            .Matches(TriggerEvent.FlowRunEnded("BUILD", "pr-opened", "task")));
        Assert.False(Trigger.ForFollow(new FlowFollow { Outcome = "PR-Opened", Flow = "build", DepthCap = 1 }).Matches(ended), "an outcome is a typed value and compares exactly");
    }

    [Fact]
    public void A_follow_never_matches_an_event_of_another_kind_carrying_the_same_fields()
    {
        Trigger follow = Trigger.ForFollow(new FlowFollow { Outcome = "pr-opened", DepthCap = 1 });

        Assert.False(follow.Matches(Event("pr-merged", ("flow", "build"), ("outcome", "pr-opened"), ("type", "task"))));
    }
}
