using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Triggers;
using Coanda.Daemon.Modules;
using Coanda.Sdk.Contract;
using Coanda.Tests.Core;
using Coanda.Tests.Sdk.Contract;
using NJsonSchema;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// How a project's resolved triggers compile to the watch request for one module: which triggers give a
/// watch, what the watch carries, which are left out and why, and how equal watches fold into one.
/// </summary>
public sealed class WatchCompilationTests
{
    private const string Tracker = "tracker";
    private const string Chat = "chat";
    private const string ProjectName = "alpha";
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly JsonSchema Schema = ModuleSchemas.Load("watch-request.schema.json");

    private static readonly DescribeAnswer Describe = new(
        ModuleApi.Version,
        "1.0.0",
        [],
        [
            new EventKindDescription("tracker/issue-opened", ["repo", "assignee"]),
            new EventKindDescription("tracker/review-requested", ["reviewer"]),
            new EventKindDescription("tracker/Bad_Kind", ["repo"]),
        ],
        [],
        new SettingsSchema([]));

    [Theory]
    [InlineData(DefinitionScope.Project)]
    [InlineData(DefinitionScope.Owner)]
    [InlineData(DefinitionScope.Module)]
    public void A_published_trigger_in_any_scope_compiles_to_one_watch_with_its_kind_and_one_filter_per_filter(DefinitionScope scope)
    {
        CompiledWatches compiled = Compile([Published(SampleTriggers.IssueOpened, scope, Tracker)], Tracker);

        Watch watch = Assert.Single(compiled.Request.Watches);
        Assert.Equal(ProjectName, compiled.Request.Project);
        Assert.Equal("tracker/issue-opened", watch.Kind);
        Assert.Equal([new WatchFilter("assignee", "pat"), new WatchFilter("repo", "coanda")], watch.Filters);
        Assert.Empty(compiled.Dropped);
    }

    [Fact]
    public void A_trigger_on_a_plain_name_a_flow_runs_end_or_another_modules_kind_compiles_to_no_watch_for_the_module()
    {
        TriggerDefinition plain = SampleTriggers.IssueOpened with { Name = "plain", EventKind = "issue-opened" };
        TriggerDefinition chat = SampleTriggers.IssueOpened with { Name = "chat", EventKind = "chat/message-posted" };

        CompiledWatches compiled = Compile(
            [Published(plain), Published(SampleTriggers.EvaluateBuild), Published(chat)], Tracker);

        Assert.Empty(compiled.Request.Watches);
        Assert.Empty(compiled.Dropped);
    }

    [Fact]
    public void A_module_scope_trigger_compiles_to_no_watch_in_a_project_the_module_that_ships_it_is_not_switched_on_for()
    {
        Definition shippedByChat = Published(SampleTriggers.IssueOpened, DefinitionScope.Module, Chat);
        Definition shippedByTracker = Published(SampleTriggers.ReviewRequested, DefinitionScope.Module, Tracker);

        CompiledWatches off = Compile([shippedByChat, shippedByTracker], Tracker, [Tracker]);
        CompiledWatches on = Compile([shippedByChat, shippedByTracker], Tracker, [Tracker, Chat]);

        Assert.Equal(["tracker/review-requested"], off.Request.Watches.Select(w => w.Kind));
        Assert.Empty(off.Dropped);
        Assert.Equal(["tracker/issue-opened", "tracker/review-requested"], on.Request.Watches.Select(w => w.Kind));
    }

    [Fact]
    public void A_draft_or_a_retired_trigger_compiles_to_no_watch()
    {
        Definition draft = Drafted(SampleTriggers.IssueOpened, DefinitionScope.Project, null);
        Definition retired = Published(SampleTriggers.ReviewRequested);
        retired.Apply(new DefinitionRetired(retired.Id, "brian", Now));

        CompiledWatches compiled = Compile([draft, retired], Tracker);

        Assert.Empty(compiled.Request.Watches);
    }

    [Fact]
    public void A_trigger_on_a_kind_describe_does_not_list_is_left_out_with_the_reason_and_the_others_are_kept()
    {
        TriggerDefinition unlisted = SampleTriggers.IssueOpened with { Name = "unlisted", EventKind = "tracker/issue-closed" };

        CompiledWatches compiled = Compile([Published(unlisted), Published(SampleTriggers.ReviewRequested)], Tracker);

        Assert.Equal("tracker/review-requested", Assert.Single(compiled.Request.Watches).Kind);
        DroppedTrigger dropped = Assert.Single(compiled.Dropped);
        Assert.Equal("unlisted", dropped.Trigger);
        Assert.Equal("module 'tracker' describes no event kind 'tracker/issue-closed'", dropped.Reason);
    }

    [Fact]
    public void A_trigger_filtering_on_a_field_describe_does_not_declare_watchable_for_its_kind_is_left_out_with_the_reason()
    {
        TriggerDefinition unwatchable = SampleTriggers.ReviewRequested with
        {
            Name = "by-label",
            Filters = SampleTriggers.Filters(("reviewer", "pat"), ("label", "urgent")),
        };

        CompiledWatches compiled = Compile([Published(unwatchable), Published(SampleTriggers.IssueOpened)], Tracker);

        Assert.Equal("tracker/issue-opened", Assert.Single(compiled.Request.Watches).Kind);
        DroppedTrigger dropped = Assert.Single(compiled.Dropped);
        Assert.Equal("by-label", dropped.Trigger);
        Assert.Equal("module 'tracker' does not declare the field 'label' watchable for 'tracker/review-requested'", dropped.Reason);
    }

    [Fact]
    public void A_trigger_whose_watch_would_break_the_schema_is_left_out_even_when_describe_lists_its_kind()
    {
        TriggerDefinition breaking = SampleTriggers.IssueOpened with
        {
            Name = "breaking",
            EventKind = "tracker/Bad_Kind",
            Filters = SampleTriggers.Filters(("repo", "coanda")),
        };

        CompiledWatches compiled = Compile([Published(breaking), Published(SampleTriggers.ReviewRequested)], Tracker);

        Assert.Equal("tracker/review-requested", Assert.Single(compiled.Request.Watches).Kind);
        DroppedTrigger dropped = Assert.Single(compiled.Dropped);
        Assert.Equal("breaking", dropped.Trigger);
        Assert.StartsWith("its watch would break the module API's watch request schema", dropped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_triggers_with_the_same_kind_and_filters_in_another_order_give_one_watch()
    {
        TriggerDefinition reversed = SampleTriggers.IssueOpened with
        {
            Name = "issue-opened-again",
            Filters = SampleTriggers.Filters(("repo", "coanda"), ("assignee", "pat")),
            Target = new TriggerTarget { EventParcel = new EventParcelTarget { Flow = "review" } },
            HourlyCap = null,
        };
        TriggerDefinition wider = SampleTriggers.IssueOpened with { Name = "any-assignee", Filters = SampleTriggers.Filters(("repo", "coanda")) };

        CompiledWatches compiled = Compile([Published(SampleTriggers.IssueOpened), Published(reversed), Published(wider)], Tracker);

        Assert.Equal(2, compiled.Request.Watches.Count);
        Assert.Single(compiled.Request.Watches, w => w.Filters.Count == 2);
        Assert.Single(compiled.Request.Watches, w => w.Filters.Count == 1);
        Assert.Empty(Schema.Validate(System.Text.Json.JsonSerializer.Serialize(compiled.Request, ModuleJson.Options)));
    }

    [Fact]
    public void A_project_with_no_trigger_on_the_modules_kinds_compiles_to_an_empty_set()
    {
        CompiledWatches compiled = Compile([Published(SampleTriggers.EvaluateBuild)], Tracker);

        Assert.Equal(ProjectName, compiled.Request.Project);
        Assert.Empty(compiled.Request.Watches);
        Assert.Empty(compiled.Dropped);
    }

    private static CompiledWatches Compile(IEnumerable<Definition> triggers, string module, string[]? switchedOn = null) =>
        WatchCompilation.Compile(ProjectOf(switchedOn ?? [Tracker]), module, Describe, triggers, Schema);

    private static Project ProjectOf(string[] modules)
    {
        Guid id = Ids.New();
        Project project = Project.Create(new ProjectRegistered(id, ProjectName, [], "brian", Now));
        project.Apply(new ProjectModulesSet(id, ProjectName, modules, "brian", Now));
        return project;
    }

    private static Definition Published(TriggerDefinition trigger, DefinitionScope scope = DefinitionScope.Project, string? module = null)
    {
        Definition definition = Drafted(trigger, scope, module);
        definition.Apply(new DefinitionPublished(definition.Id, "brian", Now));
        return definition;
    }

    private static Definition Drafted(TriggerDefinition trigger, DefinitionScope scope, string? module) =>
        Definition.Create(new DefinitionDrafted(
            Ids.New(),
            DefinitionKind.Trigger,
            trigger.Name,
            scope,
            scope == DefinitionScope.Project ? ProjectName : null,
            1,
            TriggerDefinition.CanonicalJson(trigger),
            trigger.Hash,
            null,
            "brian",
            Now,
            scope == DefinitionScope.Module ? module : null,
            scope == DefinitionScope.Module ? "1.0.0" : null));
}
