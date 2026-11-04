using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ParcelTypeFieldTests
{
    private static readonly IReadOnlySet<string> Gates = GateSet.Shipped.Names;

    [Fact]
    public void The_shipped_idea_declares_no_field_and_hashes_to_a_literal()
    {
        ShippedTypes.Idea.Fields.Should().BeEmpty();
        ShippedTypes.Idea.Hash.Should().Be("e6c4447a6bc6586d", "the idea declares no field, so its hash covers its stages, transitions, and flow bindings alone");
    }

    [Fact]
    public void The_shipped_task_declares_its_objective_criteria_non_goals_and_pull_request()
    {
        ShippedTypes.Task.Fields.Select(f => (f.Name, f.Kind, string.Join(",", f.RequiredToLeave))).Should().Equal(
            ("objective", FieldKinds.Text, "refine"),
            ("criteria", FieldKinds.TextList, "refine"),
            ("non-goals", FieldKinds.TextList, ""),
            ("pull-request", FieldKinds.Reference, "build"));
        ShippedTypes.Task.Validate(Gates);
    }

    [Fact]
    public void Fields_survive_the_canonical_json_and_are_part_of_the_hash()
    {
        ParcelType read = ParcelType.FromJson(ParcelType.CanonicalJson(ShippedTypes.Task));

        read.Fields.Should().BeEquivalentTo(ShippedTypes.Task.Fields, o => o.WithStrictOrdering());
        read.Hash.Should().Be(ShippedTypes.Task.Hash);
        ParcelType.CanonicalJson(ShippedTypes.Task).Should().Contain("\"Fields\"").And.Contain("\"RequiredToLeave\"");

        ParcelType renamed = ShippedTypes.Task with
        {
            Fields = [.. ShippedTypes.Task.Fields.Select(f => f.Name == "objective" ? f with { Name = "goal" } : f)],
        };
        ParcelType relaxed = ShippedTypes.Task with
        {
            Fields = [.. ShippedTypes.Task.Fields.Select(f => f.Name == "objective" ? f with { RequiredToLeave = [] } : f)],
        };
        ParcelType rekinded = ShippedTypes.Task with
        {
            Fields = [.. ShippedTypes.Task.Fields.Select(f => f.Name == "objective" ? f with { Kind = FieldKinds.TextList } : f)],
        };
        string[] hashes = [ShippedTypes.Task.Hash, renamed.Hash, relaxed.Hash, rekinded.Hash, (ShippedTypes.Task with { Fields = [] }).Hash];
        hashes.Should().OnlyHaveUniqueItems();
    }

    public static TheoryData<string, FieldDeclaration[], string> Refused => new()
    {
        {
            "two fields of one name",
            [Text("objective"), new FieldDeclaration { Name = "objective", Kind = FieldKinds.TextList }],
            "Parcel type 'task' declares two fields named 'objective'; a type's field names are unique."
        },
        {
            "an empty name",
            [Text("")],
            "Parcel type 'task' declares a field named ''; a field's name is plain: letters, digits, hyphens, and underscores, and not empty."
        },
        {
            "a name that is not plain",
            [Text("pull request")],
            "Parcel type 'task' declares a field named 'pull request'; a field's name is plain: letters, digits, hyphens, and underscores, and not empty."
        },
        {
            "an unknown kind",
            [new FieldDeclaration { Name = "size", Kind = "number" }],
            "Field 'size' of parcel type 'task' is of the kind 'number'; a field's kind is one of text, text-list, reference."
        },
        {
            "a stage the type does not have",
            [Text("objective", "shipping")],
            "Field 'objective' of parcel type 'task' is required to leave 'shipping', which is not one of its live stages; a field is required to leave only a stage the type has, never its terminal or paused stage."
        },
        {
            "the terminal stage",
            [Text("objective", "closed")],
            "Field 'objective' of parcel type 'task' is required to leave 'closed', which is not one of its live stages;*"
        },
        {
            "the paused stage",
            [Text("objective", "paused")],
            "Field 'objective' of parcel type 'task' is required to leave 'paused', which is not one of its live stages;*"
        },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_type_declaring_a_broken_field_is_refused_at_load_quoting_the_rule(string broken, FieldDeclaration[] fields, string rule)
    {
        broken.Should().NotBeNullOrEmpty();
        ParcelType type = ShippedTypes.Task with { Fields = fields };

        FluentActions.Invoking(() => type.Validate(Gates)).Should().Throw<DomainException>().WithMessage(rule);
    }

    [Theory]
    [InlineData("[null]", "Parcel type 'task' has a field that is null;*")]
    [InlineData("""[{"Name": "objective", "Kind": "text", "RequiredToLeave": [null]}]""", "*required to leave ''*")]
    public void A_read_field_that_is_null_or_names_a_null_stage_is_refused(string fields, string rule)
    {
        JsonObject content = JsonNode.Parse(ParcelType.CanonicalJson(ShippedTypes.Task))?.AsObject()
            ?? throw new InvalidOperationException("A type's canonical content is a JSON object.");
        content["Fields"] = JsonNode.Parse(fields);
        string replaced = content.ToJsonString();

        ParcelType read = ParcelType.FromJson(replaced);

        FluentActions.Invoking(() => read.Validate(Gates)).Should().Throw<DomainException>().WithMessage(rule);
    }

    [Fact]
    public void The_shipped_types_enforce_every_field_they_declare()
    {
        FluentActions.Invoking(() => ShippedTypes.Idea.Validate(Gates)).Should().NotThrow();
        FluentActions.Invoking(() => ShippedTypes.Task.Validate(Gates)).Should().NotThrow();
    }

    [Fact]
    public void A_type_that_declares_a_field_required_to_leave_a_stage_with_a_move_out_not_bound_to_the_field_gate_is_refused_naming_the_field_and_the_move()
    {
        ParcelType unbound = ShippedTypes.Task with
        {
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build", "review"],
                ["build"] = ["review"],
                ["review"] = ["build"],
            },
        };

        FluentActions.Invoking(() => unbound.Validate(Gates)).Should().Throw<DomainException>().WithMessage(
            "Field 'objective' of parcel type 'task' is required to leave 'refine', and the move from 'refine' to 'review' is not bound to the 'required-fields' gate; every move the table holds out of a stage a field is required to leave is bound to that gate.");
    }

    [Fact]
    public void A_table_entry_out_of_a_required_stage_into_paused_or_closed_needs_no_field_gate_since_pausing_and_closing_are_never_refused_for_an_empty_field()
    {
        ParcelType pausesInTable = ShippedTypes.Task with
        {
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build", ShippedTypes.Paused, ShippedTypes.Closed],
                ["build"] = ["review"],
                ["review"] = ["build"],
            },
        };

        FluentActions.Invoking(() => pausesInTable.Validate(Gates)).Should().NotThrow();
    }

    [Fact]
    public void A_type_with_a_stage_some_field_is_required_to_enter_and_no_move_into_it_bound_to_the_field_gate_is_refused_naming_the_stage_and_the_field()
    {
        ParcelType withQa = ShippedTypes.Task with
        {
            Stages = [.. ShippedTypes.Task.Stages, "qa"],
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build"],
                ["build"] = ["review"],
                ["review"] = ["build", "qa"],
            },
        };

        FluentActions.Invoking(() => withQa.Validate(Gates)).Should().Throw<DomainException>().WithMessage(
            "Stage 'qa' of parcel type 'task' requires 'objective', 'criteria', 'pull-request' to enter, and no move the table holds into it is bound to the 'required-fields' gate, so a resume into it would check nothing; a stage some field is required to enter has a move into it bound to that gate.");

        ParcelType guarded = withQa with
        {
            GateBindings = [.. withQa.GateBindings, new GateBinding("review", "qa", RequiredFieldsGate.GateName)],
        };
        FluentActions.Invoking(() => guarded.Validate(Gates)).Should().NotThrow();
        guarded.RequiredToEnter("qa").Should().Equal("objective", "criteria", "pull-request");
    }

    [Fact]
    public void A_field_is_required_to_enter_a_stage_only_when_every_route_to_it_requires_it_and_never_for_a_stage_no_route_reaches()
    {
        ParcelType forked = ShippedTypes.Task with
        {
            Stages = ["refine", "spike", "build", "review", "parked", ShippedTypes.Paused, ShippedTypes.Closed],
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build"],
                ["spike"] = ["build"],
                ["build"] = ["review"],
                ["review"] = ["build"],
            },
            EntryStages = ["refine", "spike"],
        };

        forked.RequiredToEnter("build").Should().BeEmpty("a route from the spike entry stage reaches build without leaving refine");
        forked.RequiredToEnter("review").Should().Equal("pull-request");
        forked.RequiredToEnter("parked").Should().BeEmpty("no route reaches it");
    }

    [Fact]
    public void A_move_out_of_a_stage_no_route_reaches_into_a_stage_some_field_is_required_to_enter_is_refused_naming_both()
    {
        ParcelType parked = ShippedTypes.Task with
        {
            Stages = ["refine", "build", "review", "parked", ShippedTypes.Paused, ShippedTypes.Closed],
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build"],
                ["build"] = ["review"],
                ["review"] = ["build"],
                ["parked"] = ["review"],
            },
        };

        FluentActions.Invoking(() => parked.Validate(Gates)).Should().Throw<DomainException>().WithMessage(
            "Stage 'review' of parcel type 'task' requires 'objective', 'criteria', 'pull-request' to enter, and the move the table holds into it from 'parked', a stage no route from an entry stage reaches, would carry a parcel resumed into 'parked' there without them; a move out of a stage no route reaches goes only into a stage that requires nothing to enter.");

        ParcelType intoRefine = parked with
        {
            Moves = new Dictionary<string, IReadOnlyList<string>>(parked.Moves) { ["parked"] = ["refine"] },
        };
        FluentActions.Invoking(() => intoRefine.Validate(Gates)).Should().NotThrow("refine requires nothing to enter");
    }

    [Fact]
    public void Drafting_or_creating_a_parcel_under_a_type_that_leaves_a_field_unenforced_is_refused()
    {
        ParcelType unenforced = ShippedTypes.Task with
        {
            GateBindings = [new GateBinding("refine", "build", RequiredFieldsGate.GateName)],
        };
        string content = ParcelType.CanonicalJson(unenforced);

        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0,
                content, unenforced.Hash, null, "brian", DateTimeOffset.UnixEpoch, Gates))
            .Should().Throw<DomainException>().WithMessage("Field 'pull-request' of parcel type 'task' is required to leave 'build', and the move from 'build' to 'review' is not bound*");
        FluentActions.Invoking(() => ParcelDecider.Create(unenforced, GateSet.Shipped, "alpha", "Work", null, "brian", DateTimeOffset.UnixEpoch))
            .Should().Throw<DomainException>().WithMessage("Field 'pull-request' of parcel type 'task'*not bound*");
    }

    private static FieldDeclaration Text(string name, params string[] requiredToLeave) =>
        new() { Name = name, Kind = FieldKinds.Text, RequiredToLeave = requiredToLeave };
}
