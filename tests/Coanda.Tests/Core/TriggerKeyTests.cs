using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Triggers;

namespace Coanda.Tests.Core;

public sealed class TriggerKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static Definition Trigger(DefinitionScope scope, string name, int version, string? project = null, string? module = null) =>
        Definition.Create(new DefinitionDrafted(
            Ids.New(), DefinitionKind.Trigger, name, scope, project, version, "{}", "hash", null, "owner", Now, module, module is null ? null : "1.0.0"));

    [Fact]
    public void A_trigger_is_named_by_its_scope_its_module_or_project_and_its_name_in_lower_case()
    {
        Assert.Equal("trigger:project:alpha/review-mine", TriggerKey.Of(Trigger(DefinitionScope.Project, "Review-Mine", 3, project: "alpha")));
        Assert.Equal("trigger:owner/review-mine", TriggerKey.Of(Trigger(DefinitionScope.Owner, "review-mine", 1)));
        Assert.Equal("trigger:module:software/rebase", TriggerKey.Of(Trigger(DefinitionScope.Module, "rebase", 7, module: "software")));
    }

    [Fact]
    public void A_triggers_name_carries_neither_a_definitions_id_nor_its_version_number()
    {
        Definition first = Trigger(DefinitionScope.Owner, "review-mine", 1);
        Definition later = Trigger(DefinitionScope.Owner, "REVIEW-MINE", 4);

        Assert.Equal(TriggerKey.Of(first), TriggerKey.Of(later));
        Assert.DoesNotContain(first.Id.ToString(), TriggerKey.Of(first), StringComparison.Ordinal);
    }

    [Fact]
    public void A_firing_adds_the_external_events_id_or_the_ended_flow_runs_id()
    {
        Guid cause = Ids.New();

        Assert.Equal($"trigger:owner/x:event:{cause}", TriggerKey.ForEvent("trigger:owner/x", cause));
        Assert.Equal($"trigger:owner/x:flow-run:{cause}", TriggerKey.ForFlowRunEnd("trigger:owner/x", cause));
    }
}
