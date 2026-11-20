using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Triggers;

namespace Coanda.Tests.Core;

/// <summary>A trigger takes the decider's paths the other three kinds of definition take, and its refusals call it a trigger.</summary>
public sealed class DefinitionDeciderTriggerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static DefinitionDrafted Draft(
        TriggerDefinition trigger, DefinitionScope scope, string? project, ForkBase? forkBase = null, IReadOnlyCollection<Definition>? moduleVersions = null) =>
        DefinitionDecider.Draft(
            DefinitionKind.Trigger, trigger.Name, scope, project, 0, TriggerDefinition.CanonicalJson(trigger), trigger.Hash, forkBase, "brian", Now,
            moduleVersions: moduleVersions);

    private static Definition Imported(TriggerDefinition trigger, string module = "tracker")
    {
        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Import(ModuleDefinition.FromTrigger(trigger), module, "1.0.0", [], "import", Now)
            ?? throw new InvalidOperationException("Nothing was imported.");
        Definition definition = Definition.Create(drafted);
        definition.Apply(published);
        return definition;
    }

    [Theory]
    [InlineData(DefinitionScope.Project, "alpha")]
    [InlineData(DefinitionScope.Owner, null)]
    public void A_trigger_is_drafted_published_and_retired_in_project_and_owner_scope(DefinitionScope scope, string? project)
    {
        TriggerDefinition trigger = SampleTriggers.IssueOpened;

        Definition definition = Definition.Create(Draft(trigger, scope, project));
        Assert.Equal(
            (DefinitionKind.Trigger, "issue-opened", scope, project, 1, TriggerDefinition.CanonicalJson(trigger), trigger.Hash, DefinitionStatus.Draft),
            (definition.Kind, definition.Name, definition.Scope, definition.Project, definition.VersionNumber, definition.Content, definition.ContentHash, definition.Status));

        definition.Apply(DefinitionDecider.Publish(definition, "owner", Now));
        Assert.Equal(DefinitionStatus.Published, definition.Status);

        definition.Apply(DefinitionDecider.Retire(definition, "owner", Now));
        Assert.Equal(DefinitionStatus.Retired, definition.Status);
    }

    [Fact]
    public void A_trigger_is_drafted_as_a_fork_of_a_modules_trigger_by_fork_base()
    {
        Definition shipped = Imported(SampleTriggers.ReviewRequested);
        var forkBase = new ForkBase("review-requested", "tracker", "1.0.0", SampleTriggers.ReviewRequested.Hash);
        TriggerDefinition fork = SampleTriggers.ReviewRequested with { Filters = SampleTriggers.Filters(("reviewer", "sam")) };

        Definition definition = Definition.Create(Draft(fork, DefinitionScope.Project, "alpha", forkBase, [shipped]));

        Assert.Equal(forkBase, definition.ForkBase);
        Assert.Equal(fork.Hash, definition.ContentHash);
        Assert.True(forkBase.IsBaseOf(shipped));
    }

    [Fact]
    public void A_fork_base_that_names_no_module_scope_trigger_is_refused_calling_it_a_trigger()
    {
        Definition shipped = Imported(SampleTriggers.ReviewRequested);
        var forkBase = new ForkBase("review-requested", "tracker", "1.0.0", SampleTriggers.IssueOpened.Hash);

        DomainException exception = Assert.Throws<DomainException>(() => Draft(SampleTriggers.ReviewRequested, DefinitionScope.Owner, null, forkBase, [shipped]));

        Assert.Contains("names no module-scope trigger;", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_a_trigger_records_it_published_in_module_scope_and_importing_the_same_content_again_records_nothing()
    {
        Definition shipped = Imported(SampleTriggers.IssueOpened);

        Assert.Equal(
            (DefinitionKind.Trigger, DefinitionScope.Module, DefinitionStatus.Published, "tracker", "1.0.0", SampleTriggers.IssueOpened.Hash),
            (shipped.Kind, shipped.Scope, shipped.Status, shipped.Module, shipped.ModuleVersion, shipped.ContentHash));
        Assert.Null(DefinitionDecider.Import(ModuleDefinition.FromTrigger(SampleTriggers.IssueOpened), "tracker", "1.0.1", [shipped], "import", Now));
    }

    [Fact]
    public void A_trigger_another_module_brought_is_refused_at_import_calling_it_a_trigger()
    {
        Definition shipped = Imported(SampleTriggers.IssueOpened);

        DomainException exception = Assert.Throws<DomainException>(() =>
            DefinitionDecider.Import(ModuleDefinition.FromTrigger(SampleTriggers.IssueOpened with { HourlyCap = 2 }), "mail", "1.0.0", [shipped], "import", Now));

        Assert.Equal(
            "Module 'mail' brings the trigger 'issue-opened', which module 'tracker' already brought; a definition's name is held by one module in module scope.",
            exception.Message);
    }

    [Fact]
    public void A_trigger_that_breaks_a_rule_is_refused_when_drafted_and_when_imported()
    {
        TriggerDefinition broken = SampleTriggers.IssueOpened with { HourlyCap = null };

        DomainException drafted = Assert.Throws<DomainException>(() => Draft(broken, DefinitionScope.Project, "alpha"));
        DomainException imported = Assert.Throws<DomainException>(() =>
            DefinitionDecider.Import(ModuleDefinition.FromTrigger(broken), "tracker", "1.0.0", [], "import", Now));

        Assert.StartsWith("Trigger 'issue-opened' creates parcels and carries no hourly cap", drafted.Message, StringComparison.Ordinal);
        Assert.Equal(drafted.Message, imported.Message);
    }

    [Fact]
    public void A_trigger_on_attention_acting_on_the_events_parcel_is_refused_when_drafted_and_when_imported()
    {
        TriggerDefinition chase = new()
        {
            Name = "chase",
            EventKind = TriggerEvent.AttentionKind,
            Target = new TriggerTarget { EventParcel = new EventParcelTarget { Flow = "judge" } },
        };

        DomainException drafted = Assert.Throws<DomainException>(() => Draft(chase, DefinitionScope.Project, "alpha"));
        DomainException imported = Assert.Throws<DomainException>(() =>
            DefinitionDecider.Import(ModuleDefinition.FromTrigger(chase), "tracker", "1.0.0", [], "import", Now));

        Assert.Contains("a pause's parcel still holds its paused flow run, so no flow can start on it", drafted.Message, StringComparison.Ordinal);
        Assert.Equal(drafted.Message, imported.Message);
    }

    [Fact]
    public void A_content_hash_that_differs_from_the_triggers_hash_is_refused()
    {
        TriggerDefinition trigger = SampleTriggers.IssueOpened;

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
            DefinitionKind.Trigger, trigger.Name, DefinitionScope.Owner, null, 0, TriggerDefinition.CanonicalJson(trigger), "0123456789abcdef", null, "brian", Now));

        Assert.Equal($"The content hash '0123456789abcdef' differs from the trigger's hash '{trigger.Hash}'.", exception.Message);
    }

    [Fact]
    public void A_definition_named_for_a_different_trigger_is_refused()
    {
        TriggerDefinition trigger = SampleTriggers.IssueOpened;

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
            DefinitionKind.Trigger, "issue-filed", DefinitionScope.Owner, null, 0, TriggerDefinition.CanonicalJson(trigger), trigger.Hash, null, "brian", Now));

        Assert.Equal("A trigger definition is named for its trigger; 'issue-filed' holds the trigger 'issue-opened'.", exception.Message);
    }

    [Fact]
    public void Content_of_another_kind_drafted_as_a_trigger_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
            DefinitionKind.Trigger, "fix", DefinitionScope.Owner, null, 0,
            AgentDefinition.CanonicalJson(SoftwareAgents.Fix), SoftwareAgents.Fix.Hash, null, "brian", Now));

        Assert.StartsWith("The content cannot be read as a trigger definition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_kind_of_definition_has_a_name_a_refusal_calls_it_by()
    {
        Assert.Equal(
            new[] { "parcel type", "flow", "agent", "trigger" },
            Enum.GetValues<DefinitionKind>().Select(DefinitionDecider.Describe));
    }
}
