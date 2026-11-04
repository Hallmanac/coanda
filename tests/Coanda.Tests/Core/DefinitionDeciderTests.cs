using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;
using Coanda.Core.Software;
using Coanda.Core.Registry;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class DefinitionDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static DefinitionDrafted DraftTask(ParcelType? type = null, DefinitionScope scope = DefinitionScope.Project, string? project = "alpha", int latest = 0)
    {
        ParcelType task = type ?? ShippedTypes.Task;
        return DefinitionDecider.Draft(
            DefinitionKind.ParcelType, task.Name, scope, scope == DefinitionScope.Project ? project : null, latest,
            ParcelType.CanonicalJson(task), task.Hash, null, "brian", Now);
    }

    private static Definition Draft(int latest = 0, DefinitionScope scope = DefinitionScope.Project) =>
        Definition.Create(DraftTask(scope: scope, latest: latest));

    [Fact]
    public void Draft_records_every_field_and_the_next_version()
    {
        ParcelType fork = ShippedTypes.Task with { Stages = [.. ShippedTypes.Task.Stages, "qa"] };
        DefinitionDrafted drafted = DefinitionDecider.Draft(
            DefinitionKind.ParcelType, "task", DefinitionScope.Project, " alpha ", 2,
            ParcelType.CanonicalJson(fork), fork.Hash, new ForkBase("task", 1), "brian", Now, shippedVersions: [new ForkBase("task", 1)]);

        Definition definition = Definition.Create(drafted);
        Assert.Equal(DefinitionKind.ParcelType, definition.Kind);
        Assert.Equal("task", definition.Name);
        Assert.Equal(DefinitionScope.Project, definition.Scope);
        Assert.Equal("alpha", definition.Project);
        Assert.Equal(3, definition.VersionNumber);
        Assert.Equal(fork.Hash, definition.ContentHash);
        Assert.Equal(ParcelType.CanonicalJson(fork), definition.Content);
        Assert.Equal(new ForkBase("task", 1), definition.ForkBase);
        Assert.Equal(DefinitionStatus.Draft, definition.Status);
    }

    [Theory]
    [InlineData("task", 2)]
    [InlineData("bug", 1)]
    [InlineData("build", 1)]
    public void A_fork_base_naming_no_shipped_definition_of_the_drafts_kind_is_refused_naming_it(string baseName, int baseVersion)
    {
        ParcelType fork = ShippedTypes.Task with { Stages = [.. ShippedTypes.Task.Stages, "qa"] };
        ForkBase[] shipped = [new("task", 1), new("idea", 1)];

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0,
                ParcelType.CanonicalJson(fork), fork.Hash, new ForkBase(baseName, baseVersion), "brian", Now, shippedVersions: shipped));
        Assert.StartsWith($"The fork base '{baseName}' version {baseVersion} of 'task' names no shipped parcel type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fork_base_is_refused_when_no_shipped_versions_are_given_and_matched_by_name_without_case()
    {
        ParcelType fork = ShippedTypes.Task with { Stages = [.. ShippedTypes.Task.Stages, "qa"] };
        string content = ParcelType.CanonicalJson(fork);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Owner, null, 0, content, fork.Hash, new ForkBase("task", 1), "brian", Now));
        Assert.StartsWith("The fork base 'task' version 1", exception.Message, StringComparison.Ordinal);

        Assert.Equal(new ForkBase("Task", 1), DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Owner, null, 0, content, fork.Hash, new ForkBase("Task", 1), "brian", Now,
                shippedVersions: [new ForkBase("task", 1)])
            .ForkBase);
    }

    [Fact]
    public void Content_round_trips_to_a_parcel_type_with_the_same_hash()
    {
        foreach (ParcelType type in ShippedTypes.All)
        {
            Assert.Equal(type.Hash, ParcelType.FromJson(ParcelType.CanonicalJson(type)).Hash);
        }
    }

    [Fact]
    public void Apply_moves_a_definition_through_draft_published_retired()
    {
        Definition definition = Draft();
        definition.Apply(DefinitionDecider.Publish(definition, "owner", Now));
        Assert.Equal(DefinitionStatus.Published, definition.Status);
        definition.Apply(DefinitionDecider.Retire(definition, "owner", Now));
        Assert.Equal(DefinitionStatus.Retired, definition.Status);
    }

    [Fact]
    public void Publishing_a_definition_that_is_not_a_draft_is_refused()
    {
        Definition definition = Draft();
        definition.Apply(DefinitionDecider.Publish(definition, "owner", Now));

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Publish(definition, "owner", Now));
        Assert.Matches(@"(?s)^Only a draft can be published.*is published.*$", exception.Message);

        definition.Apply(DefinitionDecider.Retire(definition, "owner", Now));
        DomainException exception2 = Assert.Throws<DomainException>(() => DefinitionDecider.Publish(definition, "owner", Now));
        Assert.Matches(@"(?s)^Only a draft can be published.*is retired.*$", exception2.Message);
    }

    [Fact]
    public void Retiring_a_definition_that_is_not_published_is_refused()
    {
        Definition definition = Draft();
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Retire(definition, "owner", Now));
        Assert.Matches(@"(?s)^Only a published definition can be retired.*is a draft.*$", exception.Message);
    }

    [Fact]
    public void Retiring_a_shipped_definition_is_refused()
    {
        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Seed(ShippedDefinition.FromParcelType(ShippedTypes.Task), 0, "seed", Now);
        Definition shipped = Definition.Create(drafted);
        shipped.Apply(published);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Retire(shipped, "owner", Now));
        Assert.StartsWith("A shipped definition cannot be retired", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Drafting_into_shipped_scope_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Shipped, null, 0,
                ParcelType.CanonicalJson(ShippedTypes.Task), ShippedTypes.Task.Hash, null, "brian", Now));
        Assert.StartsWith("A definition cannot be drafted into shipped scope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seeding_produces_the_drafted_and_published_events_together_in_shipped_scope()
    {
        ShippedDefinition shipped = ShippedDefinition.FromParcelType(ShippedTypes.Idea);

        (DefinitionDrafted drafted, DefinitionPublished published) = DefinitionDecider.Seed(shipped, 1, "seed", Now);

        Assert.Equal(DefinitionScope.Shipped, drafted.Scope);
        Assert.Null(drafted.Project);
        Assert.Equal(2, drafted.Version);
        Assert.Equal(ShippedTypes.Idea.Hash, drafted.ContentHash);
        Assert.Equal(drafted.DefinitionId, published.DefinitionId);

        Definition definition = Definition.Create(drafted);
        definition.Apply(published);
        Assert.Equal(DefinitionStatus.Published, definition.Status);
    }

    [Fact]
    public void A_parcel_type_whose_content_cannot_be_read_is_refused()
    {
        foreach (string content in new[] { "not json", "{}", "null", """{"Name":"task","Stages":null}""" })
        {
            DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                    DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, "abc", null, "brian", Now));
            Assert.Matches(@"(?s)^The content.*parcel type.*$", exception.Message);
        }
    }

    [Fact]
    public void A_parcel_type_that_fails_validation_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with { TerminalStage = "finished" };

        DomainException exception = Assert.Throws<DomainException>(() => DraftTask(broken));
        Assert.Contains("terminal stage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parcel_type_whose_content_hash_differs_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0,
                ParcelType.CanonicalJson(ShippedTypes.Task), ShippedTypes.Idea.Hash, null, "brian", Now));
        Assert.StartsWith($"The content hash '{ShippedTypes.Idea.Hash}' differs from the parcel type's hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_named_for_a_different_type_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "bug", DefinitionScope.Project, "alpha", 0,
                ParcelType.CanonicalJson(ShippedTypes.Task), ShippedTypes.Task.Hash, null, "brian", Now));
        Assert.StartsWith("A parcel-type definition is named for its type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_and_project_must_agree()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DraftTask(project: " "));
        Assert.StartsWith("A project-scope definition belongs to a project", exception.Message, StringComparison.Ordinal);

        DomainException exception2 = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Owner, "alpha", 0,
                ParcelType.CanonicalJson(ShippedTypes.Task), ShippedTypes.Task.Hash, null, "brian", Now));
        Assert.StartsWith("Only a project-scope definition belongs to a project", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_and_retire_are_open_only_to_the_owner_and_draft_to_everyone()
    {
        var member = new Principal("pat", Role.Member);
        var agent = new Principal("build-session", Role.Agent);

        foreach (Principal principal in new[] { member, agent })
        {
            Assert.NotNull(CommandRegistry.Require("definition.draft", principal));
            DomainException exception = Assert.Throws<DomainException>(() => CommandRegistry.Require("definition.publish", principal));
            Assert.Equal($"'definition.publish' is not open to the {principal.Role} role.", exception.Message);
            DomainException exception2 = Assert.Throws<DomainException>(() => CommandRegistry.Require("definition.retire", principal));
            Assert.Equal($"'definition.retire' is not open to the {principal.Role} role.", exception2.Message);
        }

        Assert.NotNull(CommandRegistry.Require("definition.publish", Principal.LocalOwner));
        Assert.NotNull(CommandRegistry.Require("definition.retire", Principal.LocalOwner));
    }

    private static DefinitionDrafted DraftFlow(Flow flow, string? name = null, string? hash = null, IReadOnlySet<string>? systemSteps = null) =>
        DefinitionDecider.Draft(
            DefinitionKind.Flow, name ?? flow.Name, DefinitionScope.Project, "alpha", 0,
            Flow.CanonicalJson(flow), hash ?? flow.Hash, null, "brian", Now, knownSystemSteps: systemSteps ?? ShippedSystemSteps.Names);

    [Fact]
    public void A_valid_flow_is_drafted_with_its_hash_and_content_that_reads_back()
    {
        DefinitionDrafted drafted = DraftFlow(ShippedFlows.Build);

        Assert.Equal(DefinitionKind.Flow, drafted.Kind);
        Assert.Equal(ShippedFlows.Build.Hash, drafted.ContentHash);
        Assert.Equal(ShippedFlows.Build.Hash, Flow.FromJson(drafted.Content).Hash);
    }

    [Fact]
    public void A_flow_that_fails_validation_is_refused_quoting_the_validation_message()
    {
        Flow broken = ShippedFlows.Build with { Budget = 0 };

        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(broken));
        Assert.Equal("Flow 'build' needs a budget greater than zero; every flow declares one.", exception.Message);
    }

    [Fact]
    public void A_flow_naming_a_system_step_absent_from_the_names_passed_in_is_refused_even_when_a_catalog_holds_it()
    {
        var withoutGates = new HashSet<string>(ShippedSystemSteps.Names.Where(a => a != SoftwareSystemSteps.Gates), StringComparer.Ordinal);
        var withoutMove = new HashSet<string>(ShippedSystemSteps.Names.Where(a => a != CoreSystemSteps.MoveParcel), StringComparer.Ordinal);

        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(ShippedFlows.Build, systemSteps: withoutGates));
        Assert.Contains("names the system step 'gates', which is not a known system step", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(() => DraftFlow(ShippedFlows.Build, systemSteps: withoutMove));
        Assert.Contains("names the system step 'move-parcel', which is not a known system step", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_naming_a_system_step_among_the_names_passed_in_is_drafted_even_when_neither_catalog_holds_it()
    {
        FlowStep[] steps = [.. ShippedFlows.Build.Steps.Select(s => s.SystemStep == SoftwareSystemSteps.Gates ? s with { SystemStep = "teleport" } : s)];
        Flow teleporting = ShippedFlows.Build with { Steps = steps };
        Assert.DoesNotContain("teleport", CoreSystemSteps.All.Concat(SoftwareSystemSteps.All));

        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(teleporting));
        Assert.Contains("names the system step 'teleport', which is not a known system step", exception.Message, StringComparison.Ordinal);

        DefinitionDrafted drafted = DraftFlow(teleporting, systemSteps: new HashSet<string>([.. ShippedSystemSteps.Names, "teleport"], StringComparer.Ordinal));
        Assert.Equal(teleporting.Hash, drafted.ContentHash);
    }

    [Fact]
    public void Seeding_a_flow_validates_it_against_the_names_passed_in()
    {
        ShippedDefinition build = ShippedDefinition.FromFlow(ShippedFlows.Build);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Seed(build, 0, "seed", Now));
        Assert.Contains("which is not a known system step", exception.Message, StringComparison.Ordinal);
        Assert.Equal(ShippedFlows.Build.Hash, DefinitionDecider.Seed(build, 0, "seed", Now, ShippedSystemSteps.Names).Drafted.ContentHash);
    }

    [Fact]
    public void A_flow_whose_content_hash_differs_from_its_content_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(ShippedFlows.Build, hash: "0123456789abcdef"));
        Assert.StartsWith("The content hash '0123456789abcdef' differs from the flow's hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_whose_content_cannot_be_read_is_refused()
    {
        string[] contents = ["not json", "null", "{\"Name\":\"build\",\"Steps\":[{\"Name\":\"s\",\"Condition\":\"x\"}]}"];
        foreach (string content in contents)
        {
            DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                    DefinitionKind.Flow, "build", DefinitionScope.Project, "alpha", 0, content, "abc", null, "brian", Now));
            Assert.Matches(@"(?s)^The content.*flow.*$", exception.Message);
        }
    }

    [Fact]
    public void A_flow_definition_named_for_a_different_flow_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(ShippedFlows.Build, name: "ship"));
        Assert.StartsWith("A flow definition is named for its flow", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seeding_a_shipped_flow_produces_a_published_shipped_scope_definition()
    {
        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Seed(ShippedDefinition.FromFlow(ShippedFlows.Discovery), 0, "seed", Now, ShippedSystemSteps.Names);

        Assert.Equal(DefinitionKind.Flow, drafted.Kind);
        Assert.Equal(DefinitionScope.Shipped, drafted.Scope);
        Assert.Equal(ShippedFlows.Discovery.Hash, drafted.ContentHash);
        Assert.Equal(drafted.DefinitionId, published.DefinitionId);
    }

    [Fact]
    public void A_drafted_parcel_type_is_validated_against_the_gate_names_it_is_given()
    {
        ParcelType gated = ShippedTypes.Task with { Fields = [], GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };
        string content = ParcelType.CanonicalJson(gated);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, gated.Hash, null, "brian", Now));
        Assert.Contains("binds the gate 'has-an-owner', which is not a gate this daemon runs", exception.Message, StringComparison.Ordinal);

        DefinitionDrafted drafted = DefinitionDecider.Draft(
            DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, gated.Hash, null, "brian", Now,
            new HashSet<string>(StringComparer.Ordinal) { "has-an-owner" });
        Assert.Equal(gated.Hash, drafted.ContentHash);
    }
}
