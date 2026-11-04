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
        definition.Kind.Should().Be(DefinitionKind.ParcelType);
        definition.Name.Should().Be("task");
        definition.Scope.Should().Be(DefinitionScope.Project);
        definition.Project.Should().Be("alpha");
        definition.VersionNumber.Should().Be(3);
        definition.ContentHash.Should().Be(fork.Hash);
        definition.Content.Should().Be(ParcelType.CanonicalJson(fork));
        definition.ForkBase.Should().Be(new ForkBase("task", 1));
        definition.Status.Should().Be(DefinitionStatus.Draft);
    }

    [Theory]
    [InlineData("task", 2)]
    [InlineData("bug", 1)]
    [InlineData("build", 1)]
    public void A_fork_base_naming_no_shipped_definition_of_the_drafts_kind_is_refused_naming_it(string baseName, int baseVersion)
    {
        ParcelType fork = ShippedTypes.Task with { Stages = [.. ShippedTypes.Task.Stages, "qa"] };
        ForkBase[] shipped = [new("task", 1), new("idea", 1)];

        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0,
                ParcelType.CanonicalJson(fork), fork.Hash, new ForkBase(baseName, baseVersion), "brian", Now, shippedVersions: shipped))
            .Should().Throw<DomainException>()
            .WithMessage($"The fork base '{baseName}' version {baseVersion} of 'task' names no shipped parcel type*");
    }

    [Fact]
    public void A_fork_base_is_refused_when_no_shipped_versions_are_given_and_matched_by_name_without_case()
    {
        ParcelType fork = ShippedTypes.Task with { Stages = [.. ShippedTypes.Task.Stages, "qa"] };
        string content = ParcelType.CanonicalJson(fork);

        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Owner, null, 0, content, fork.Hash, new ForkBase("task", 1), "brian", Now))
            .Should().Throw<DomainException>().WithMessage("The fork base 'task' version 1*");

        DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Owner, null, 0, content, fork.Hash, new ForkBase("Task", 1), "brian", Now,
                shippedVersions: [new ForkBase("task", 1)])
            .ForkBase.Should().Be(new ForkBase("Task", 1));
    }

    [Fact]
    public void Content_round_trips_to_a_parcel_type_with_the_same_hash()
    {
        foreach (ParcelType type in ShippedTypes.All)
        {
            ParcelType.FromJson(ParcelType.CanonicalJson(type)).Hash.Should().Be(type.Hash);
        }
    }

    [Fact]
    public void Apply_moves_a_definition_through_draft_published_retired()
    {
        Definition definition = Draft();
        definition.Apply(DefinitionDecider.Publish(definition, "owner", Now));
        definition.Status.Should().Be(DefinitionStatus.Published);
        definition.Apply(DefinitionDecider.Retire(definition, "owner", Now));
        definition.Status.Should().Be(DefinitionStatus.Retired);
    }

    [Fact]
    public void Publishing_a_definition_that_is_not_a_draft_is_refused()
    {
        Definition definition = Draft();
        definition.Apply(DefinitionDecider.Publish(definition, "owner", Now));

        FluentActions.Invoking(() => DefinitionDecider.Publish(definition, "owner", Now))
            .Should().Throw<DomainException>().WithMessage("Only a draft can be published*is published*");

        definition.Apply(DefinitionDecider.Retire(definition, "owner", Now));
        FluentActions.Invoking(() => DefinitionDecider.Publish(definition, "owner", Now))
            .Should().Throw<DomainException>().WithMessage("Only a draft can be published*is retired*");
    }

    [Fact]
    public void Retiring_a_definition_that_is_not_published_is_refused()
    {
        Definition definition = Draft();
        FluentActions.Invoking(() => DefinitionDecider.Retire(definition, "owner", Now))
            .Should().Throw<DomainException>().WithMessage("Only a published definition can be retired*is a draft*");
    }

    [Fact]
    public void Retiring_a_shipped_definition_is_refused()
    {
        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Seed(ShippedDefinition.FromParcelType(ShippedTypes.Task), 0, "seed", Now);
        Definition shipped = Definition.Create(drafted);
        shipped.Apply(published);

        FluentActions.Invoking(() => DefinitionDecider.Retire(shipped, "owner", Now))
            .Should().Throw<DomainException>().WithMessage("A shipped definition cannot be retired*");
    }

    [Fact]
    public void Drafting_into_shipped_scope_is_refused()
    {
        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Shipped, null, 0,
                ParcelType.CanonicalJson(ShippedTypes.Task), ShippedTypes.Task.Hash, null, "brian", Now))
            .Should().Throw<DomainException>().WithMessage("A definition cannot be drafted into shipped scope*");
    }

    [Fact]
    public void Seeding_produces_the_drafted_and_published_events_together_in_shipped_scope()
    {
        ShippedDefinition shipped = ShippedDefinition.FromParcelType(ShippedTypes.Idea);

        (DefinitionDrafted drafted, DefinitionPublished published) = DefinitionDecider.Seed(shipped, 1, "seed", Now);

        drafted.Scope.Should().Be(DefinitionScope.Shipped);
        drafted.Project.Should().BeNull();
        drafted.Version.Should().Be(2);
        drafted.ContentHash.Should().Be(ShippedTypes.Idea.Hash);
        published.DefinitionId.Should().Be(drafted.DefinitionId);

        Definition definition = Definition.Create(drafted);
        definition.Apply(published);
        definition.Status.Should().Be(DefinitionStatus.Published);
    }

    [Fact]
    public void A_parcel_type_whose_content_cannot_be_read_is_refused()
    {
        foreach (string content in new[] { "not json", "{}", "null", """{"Name":"task","Stages":null}""" })
        {
            FluentActions.Invoking(() => DefinitionDecider.Draft(
                    DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, "abc", null, "brian", Now))
                .Should().Throw<DomainException>().WithMessage("The content*parcel type*");
        }
    }

    [Fact]
    public void A_parcel_type_that_fails_validation_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with { TerminalStage = "finished" };

        FluentActions.Invoking(() => DraftTask(broken))
            .Should().Throw<DomainException>().WithMessage("*terminal stage*");
    }

    [Fact]
    public void A_parcel_type_whose_content_hash_differs_is_refused()
    {
        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0,
                ParcelType.CanonicalJson(ShippedTypes.Task), ShippedTypes.Idea.Hash, null, "brian", Now))
            .Should().Throw<DomainException>().WithMessage($"The content hash '{ShippedTypes.Idea.Hash}' differs from the parcel type's hash*");
    }

    [Fact]
    public void A_definition_named_for_a_different_type_is_refused()
    {
        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "bug", DefinitionScope.Project, "alpha", 0,
                ParcelType.CanonicalJson(ShippedTypes.Task), ShippedTypes.Task.Hash, null, "brian", Now))
            .Should().Throw<DomainException>().WithMessage("A parcel-type definition is named for its type*");
    }

    [Fact]
    public void Scope_and_project_must_agree()
    {
        FluentActions.Invoking(() => DraftTask(project: " "))
            .Should().Throw<DomainException>().WithMessage("A project-scope definition belongs to a project*");

        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Owner, "alpha", 0,
                ParcelType.CanonicalJson(ShippedTypes.Task), ShippedTypes.Task.Hash, null, "brian", Now))
            .Should().Throw<DomainException>().WithMessage("Only a project-scope definition belongs to a project*");
    }

    [Fact]
    public void Publish_and_retire_are_open_only_to_the_owner_and_draft_to_everyone()
    {
        var member = new Principal("pat", Role.Member);
        var agent = new Principal("build-session", Role.Agent);

        foreach (Principal principal in new[] { member, agent })
        {
            CommandRegistry.Require("definition.draft", principal).Should().NotBeNull();
            FluentActions.Invoking(() => CommandRegistry.Require("definition.publish", principal))
                .Should().Throw<DomainException>().WithMessage($"'definition.publish' is not open to the {principal.Role} role.");
            FluentActions.Invoking(() => CommandRegistry.Require("definition.retire", principal))
                .Should().Throw<DomainException>().WithMessage($"'definition.retire' is not open to the {principal.Role} role.");
        }

        CommandRegistry.Require("definition.publish", Principal.LocalOwner).Should().NotBeNull();
        CommandRegistry.Require("definition.retire", Principal.LocalOwner).Should().NotBeNull();
    }

    private static DefinitionDrafted DraftFlow(Flow flow, string? name = null, string? hash = null, IReadOnlySet<string>? systemSteps = null) =>
        DefinitionDecider.Draft(
            DefinitionKind.Flow, name ?? flow.Name, DefinitionScope.Project, "alpha", 0,
            Flow.CanonicalJson(flow), hash ?? flow.Hash, null, "brian", Now, knownSystemSteps: systemSteps ?? ShippedSystemSteps.Names);

    [Fact]
    public void A_valid_flow_is_drafted_with_its_hash_and_content_that_reads_back()
    {
        DefinitionDrafted drafted = DraftFlow(ShippedFlows.Build);

        drafted.Kind.Should().Be(DefinitionKind.Flow);
        drafted.ContentHash.Should().Be(ShippedFlows.Build.Hash);
        Flow.FromJson(drafted.Content).Hash.Should().Be(ShippedFlows.Build.Hash);
    }

    [Fact]
    public void A_flow_that_fails_validation_is_refused_quoting_the_validation_message()
    {
        Flow broken = ShippedFlows.Build with { Budget = 0 };

        FluentActions.Invoking(() => DraftFlow(broken))
            .Should().Throw<DomainException>().WithMessage("Flow 'build' needs a budget greater than zero; every flow declares one.");
    }

    [Fact]
    public void A_flow_naming_a_system_step_absent_from_the_names_passed_in_is_refused_even_when_a_catalog_holds_it()
    {
        var withoutGates = new HashSet<string>(ShippedSystemSteps.Names.Where(a => a != SoftwareSystemSteps.Gates), StringComparer.Ordinal);
        var withoutMove = new HashSet<string>(ShippedSystemSteps.Names.Where(a => a != CoreSystemSteps.MoveParcel), StringComparer.Ordinal);

        FluentActions.Invoking(() => DraftFlow(ShippedFlows.Build, systemSteps: withoutGates))
            .Should().Throw<DomainException>().WithMessage("*names the system step 'gates', which is not a known system step*");
        FluentActions.Invoking(() => DraftFlow(ShippedFlows.Build, systemSteps: withoutMove))
            .Should().Throw<DomainException>().WithMessage("*names the system step 'move-parcel', which is not a known system step*");
    }

    [Fact]
    public void A_flow_naming_a_system_step_among_the_names_passed_in_is_drafted_even_when_neither_catalog_holds_it()
    {
        FlowStep[] steps = [.. ShippedFlows.Build.Steps.Select(s => s.SystemStep == SoftwareSystemSteps.Gates ? s with { SystemStep = "teleport" } : s)];
        Flow teleporting = ShippedFlows.Build with { Steps = steps };
        CoreSystemSteps.All.Concat(SoftwareSystemSteps.All).Should().NotContain("teleport");

        FluentActions.Invoking(() => DraftFlow(teleporting))
            .Should().Throw<DomainException>().WithMessage("*names the system step 'teleport', which is not a known system step*");

        DefinitionDrafted drafted = DraftFlow(teleporting, systemSteps: new HashSet<string>([.. ShippedSystemSteps.Names, "teleport"], StringComparer.Ordinal));
        drafted.ContentHash.Should().Be(teleporting.Hash);
    }

    [Fact]
    public void Seeding_a_flow_validates_it_against_the_names_passed_in()
    {
        ShippedDefinition build = ShippedDefinition.FromFlow(ShippedFlows.Build);

        FluentActions.Invoking(() => DefinitionDecider.Seed(build, 0, "seed", Now))
            .Should().Throw<DomainException>().WithMessage("*which is not a known system step*");
        DefinitionDecider.Seed(build, 0, "seed", Now, ShippedSystemSteps.Names).Drafted.ContentHash.Should().Be(ShippedFlows.Build.Hash);
    }

    [Fact]
    public void A_flow_whose_content_hash_differs_from_its_content_is_refused()
    {
        FluentActions.Invoking(() => DraftFlow(ShippedFlows.Build, hash: "0123456789abcdef"))
            .Should().Throw<DomainException>().WithMessage("The content hash '0123456789abcdef' differs from the flow's hash*");
    }

    [Fact]
    public void A_flow_whose_content_cannot_be_read_is_refused()
    {
        string[] contents = ["not json", "null", "{\"Name\":\"build\",\"Steps\":[{\"Name\":\"s\",\"Condition\":\"x\"}]}"];
        foreach (string content in contents)
        {
            FluentActions.Invoking(() => DefinitionDecider.Draft(
                    DefinitionKind.Flow, "build", DefinitionScope.Project, "alpha", 0, content, "abc", null, "brian", Now))
                .Should().Throw<DomainException>().WithMessage("The content*flow*");
        }
    }

    [Fact]
    public void A_flow_definition_named_for_a_different_flow_is_refused()
    {
        FluentActions.Invoking(() => DraftFlow(ShippedFlows.Build, name: "ship"))
            .Should().Throw<DomainException>().WithMessage("A flow definition is named for its flow*");
    }

    [Fact]
    public void Seeding_a_shipped_flow_produces_a_published_shipped_scope_definition()
    {
        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Seed(ShippedDefinition.FromFlow(ShippedFlows.Discovery), 0, "seed", Now, ShippedSystemSteps.Names);

        drafted.Kind.Should().Be(DefinitionKind.Flow);
        drafted.Scope.Should().Be(DefinitionScope.Shipped);
        drafted.ContentHash.Should().Be(ShippedFlows.Discovery.Hash);
        published.DefinitionId.Should().Be(drafted.DefinitionId);
    }

    [Fact]
    public void A_drafted_parcel_type_is_validated_against_the_gate_names_it_is_given()
    {
        ParcelType gated = ShippedTypes.Task with { Fields = [], GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };
        string content = ParcelType.CanonicalJson(gated);

        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, gated.Hash, null, "brian", Now))
            .Should().Throw<DomainException>().WithMessage("*binds the gate 'has-an-owner', which is not a gate this daemon runs*");

        DefinitionDrafted drafted = DefinitionDecider.Draft(
            DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, gated.Hash, null, "brian", Now,
            new HashSet<string>(StringComparer.Ordinal) { "has-an-owner" });
        drafted.ContentHash.Should().Be(gated.Hash);
    }
}
