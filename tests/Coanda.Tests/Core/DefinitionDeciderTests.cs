using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Contours;
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
        ParcelType fork = ShippedTypes.Task with { States = [.. ShippedTypes.Task.States, "qa"] };
        DefinitionDrafted drafted = DefinitionDecider.Draft(
            DefinitionKind.ParcelType, "task", DefinitionScope.Project, " alpha ", 2,
            ParcelType.CanonicalJson(fork), fork.Hash, new ForkBase("task", 1), "brian", Now);

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
        foreach (string content in new[] { "not json", "{}", "null", """{"Name":"task","States":null}""" })
        {
            FluentActions.Invoking(() => DefinitionDecider.Draft(
                    DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, "abc", null, "brian", Now))
                .Should().Throw<DomainException>().WithMessage("The content*parcel type*");
        }
    }

    [Fact]
    public void A_parcel_type_that_fails_validation_is_refused()
    {
        ParcelType broken = ShippedTypes.Task with { TerminalState = "finished" };

        FluentActions.Invoking(() => DraftTask(broken))
            .Should().Throw<DomainException>().WithMessage("*terminal state*");
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

    private static DefinitionDrafted DraftContour(Contour contour, string? name = null, string? hash = null) =>
        DefinitionDecider.Draft(
            DefinitionKind.Contour, name ?? contour.Name, DefinitionScope.Project, "alpha", 0,
            Contour.CanonicalJson(contour), hash ?? contour.Hash, null, "brian", Now);

    [Fact]
    public void A_valid_contour_is_drafted_with_its_hash_and_content_that_reads_back()
    {
        DefinitionDrafted drafted = DraftContour(ShippedContours.Build);

        drafted.Kind.Should().Be(DefinitionKind.Contour);
        drafted.ContentHash.Should().Be(ShippedContours.Build.Hash);
        Contour.FromJson(drafted.Content).Hash.Should().Be(ShippedContours.Build.Hash);
    }

    [Fact]
    public void A_contour_that_fails_validation_is_refused_quoting_the_validation_message()
    {
        Contour broken = ShippedContours.Build with { Budget = 0 };

        FluentActions.Invoking(() => DraftContour(broken))
            .Should().Throw<DomainException>().WithMessage("Contour 'build' needs a budget greater than zero; every contour declares one.");
    }

    [Fact]
    public void A_contour_naming_an_action_in_neither_catalog_is_refused()
    {
        ContourStep[] steps = [.. ShippedContours.Build.Steps.Select(s => s.Action == SoftwareActions.Gates ? s with { Action = "teleport" } : s)];

        FluentActions.Invoking(() => DraftContour(ShippedContours.Build with { Steps = steps }))
            .Should().Throw<DomainException>().WithMessage("*names the action 'teleport', which is not a known action*");
    }

    [Fact]
    public void A_contour_whose_content_hash_differs_from_its_content_is_refused()
    {
        FluentActions.Invoking(() => DraftContour(ShippedContours.Build, hash: "0123456789abcdef"))
            .Should().Throw<DomainException>().WithMessage("The content hash '0123456789abcdef' differs from the contour's hash*");
    }

    [Fact]
    public void A_contour_whose_content_cannot_be_read_is_refused()
    {
        string[] contents = ["not json", "null", "{\"Name\":\"build\",\"Steps\":[{\"Name\":\"s\",\"Condition\":\"x\"}]}"];
        foreach (string content in contents)
        {
            FluentActions.Invoking(() => DefinitionDecider.Draft(
                    DefinitionKind.Contour, "build", DefinitionScope.Project, "alpha", 0, content, "abc", null, "brian", Now))
                .Should().Throw<DomainException>().WithMessage("The content*contour*");
        }
    }

    [Fact]
    public void A_contour_definition_named_for_a_different_contour_is_refused()
    {
        FluentActions.Invoking(() => DraftContour(ShippedContours.Build, name: "ship"))
            .Should().Throw<DomainException>().WithMessage("A contour definition is named for its contour*");
    }

    [Fact]
    public void Seeding_a_shipped_contour_produces_a_published_shipped_scope_definition()
    {
        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Seed(ShippedDefinition.FromContour(ShippedContours.Discovery), 0, "seed", Now);

        drafted.Kind.Should().Be(DefinitionKind.Contour);
        drafted.Scope.Should().Be(DefinitionScope.Shipped);
        drafted.ContentHash.Should().Be(ShippedContours.Discovery.Hash);
        published.DefinitionId.Should().Be(drafted.DefinitionId);
    }

    [Fact]
    public void A_drafted_parcel_type_is_validated_against_the_gate_names_it_is_given()
    {
        ParcelType gated = ShippedTypes.Task with { GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };
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
