using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ParcelTypeFieldTests
{
    private static readonly IReadOnlySet<string> Gates = GateSet.Shipped.Names;

    [Fact]
    public void The_shipped_idea_hashes_as_it_did_before_fields_existed()
    {
        ShippedTypes.Idea.Fields.Should().BeEmpty();
        ShippedTypes.Idea.Hash.Should().Be("2494543b0e6fbb54", "the idea declares no field, so its hash is the one it had at the branch base");
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
            "a state the type does not have",
            [Text("objective", "shipping")],
            "Field 'objective' of parcel type 'task' is required to leave 'shipping', which is not one of its live states; a field is required to leave only a state the type has, never its terminal or paused state."
        },
        {
            "the terminal state",
            [Text("objective", "closed")],
            "Field 'objective' of parcel type 'task' is required to leave 'closed', which is not one of its live states;*"
        },
        {
            "the paused state",
            [Text("objective", "paused")],
            "Field 'objective' of parcel type 'task' is required to leave 'paused', which is not one of its live states;*"
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
    public void A_read_field_that_is_null_or_names_a_null_state_is_refused(string fields, string rule)
    {
        JsonObject content = JsonNode.Parse(ParcelType.CanonicalJson(ShippedTypes.Task))?.AsObject()
            ?? throw new InvalidOperationException("A type's canonical content is a JSON object.");
        content["Fields"] = JsonNode.Parse(fields);
        string replaced = content.ToJsonString();

        ParcelType read = ParcelType.FromJson(replaced);

        FluentActions.Invoking(() => read.Validate(Gates)).Should().Throw<DomainException>().WithMessage(rule);
    }

    private static FieldDeclaration Text(string name, params string[] requiredToLeave) =>
        new() { Name = name, Kind = FieldKinds.Text, RequiredToLeave = requiredToLeave };
}
