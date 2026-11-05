using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ParcelTypeFieldTests
{
    private static readonly IReadOnlySet<string> Gates = GateSet.Shipped.Names;

    [Fact]
    public void The_shipped_idea_declares_no_field_and_hashes_to_a_literal()
    {
        Assert.Empty(SoftwareTypes.Idea.Fields);
        // the idea declares no field, so its hash covers its stages, transitions, and flow bindings alone
        Assert.Equal("e6c4447a6bc6586d", SoftwareTypes.Idea.Hash);
    }

    [Fact]
    public void The_shipped_task_names_the_software_module_as_the_owner_of_its_repos_field_alone_and_hashes_to_its_literal()
    {
        Assert.Equal([("repos", "software")], SoftwareTypes.Task.Fields.Where(f => f.Owner is not null).Select(f => (f.Name, f.Owner)));
        // without the repos field, the task's hash is the one it had before a field could name an owner
        Assert.Equal("f5bc1ad145ab7a9f", SoftwareTypes.UnownedTask.Hash);
        Assert.Equal("3e16509d2543dbb9", SoftwareTypes.Task.Hash);
        Assert.DoesNotContain("\"Owner\"", ParcelType.CanonicalJson(SoftwareTypes.UnownedTask), StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_task_marks_its_repos_field_as_its_tag_field_and_without_the_mark_hashes_as_it_did_before_the_mark()
    {
        Assert.Equal("repos", SoftwareTypes.Task.TagField?.Name);
        ParcelType unmarked = SoftwareTypes.Task with { Fields = [.. SoftwareTypes.Task.Fields.Select(f => f with { Tag = false })] };

        // the task's hash before the mark existed, which the live node's first module-scope task holds
        Assert.Equal("ea1d4a852606b5ea", unmarked.Hash);
        Assert.Null(unmarked.TagField);
        Assert.DoesNotContain("\"Tag\"", ParcelType.CanonicalJson(unmarked), StringComparison.Ordinal);
        Assert.Contains("\"Tag\": true", ParcelType.CanonicalJson(SoftwareTypes.Task), StringComparison.Ordinal);
        Assert.Equal("repos", ParcelType.FromJson(ParcelType.CanonicalJson(SoftwareTypes.Task)).TagField?.Name);
    }

    [Fact]
    public void A_type_marking_two_tag_fields_or_one_that_is_not_a_module_owned_text_list_is_refused()
    {
        FieldDeclaration repos = SoftwareTypes.Task.Fields.Single(f => f.Tag);
        ParcelType twice = SoftwareTypes.Task with { Fields = [.. SoftwareTypes.Task.Fields, repos with { Name = "mirrors" }] };
        ParcelType unowned = SoftwareTypes.Task with { Fields = [.. SoftwareTypes.Task.Fields.Select(f => f.Tag ? f with { Owner = null } : f)] };
        ParcelType text = SoftwareTypes.Task with { Fields = [.. SoftwareTypes.Task.Fields.Select(f => f.Tag ? f with { Kind = FieldKinds.Text } : f)] };
        ParcelType reference = SoftwareTypes.Task with { Fields = [.. SoftwareTypes.Task.Fields.Select(f => f.Tag ? f with { Kind = FieldKinds.Reference } : f)] };

        SoftwareTypes.Task.Validate(Gates);
        Assert.Equal(
            "Parcel type 'task' marks 'repos', 'mirrors' as its tag field; a type marks at most one.",
            Assert.Throws<DomainException>(() => twice.Validate(Gates)).Message);
        Assert.Equal(
            "Field 'repos' of parcel type 'task' is marked as the tag field and is a text-list; the tag field is a module-owned text-list, since its values are the repos that carry a parcel's events.",
            Assert.Throws<DomainException>(() => unowned.Validate(Gates)).Message);
        Assert.Contains("is a module-owned text;", Assert.Throws<DomainException>(() => text.Validate(Gates)).Message, StringComparison.Ordinal);
        Assert.Contains("is a module-owned reference;", Assert.Throws<DomainException>(() => reference.Validate(Gates)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fields_owner_survives_the_canonical_json_and_is_part_of_the_hash()
    {
        ParcelType owned = WithObjectiveOwnedBy("software");
        ParcelType ownedByAnother = WithObjectiveOwnedBy("jira");

        ParcelType read = ParcelType.FromJson(ParcelType.CanonicalJson(owned));

        Assert.Equal("software", read.Field("objective")?.Owner);
        Assert.Null(read.Field("criteria")?.Owner);
        Assert.Equal(owned.Hash, read.Hash);
        Assert.Contains("\"Owner\": \"software\"", ParcelType.CanonicalJson(owned), StringComparison.Ordinal);
        Assert.Distinct((string[])[SoftwareTypes.Task.Hash, owned.Hash, ownedByAnother.Hash]);
        owned.Validate(Gates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("software/github")]
    [InlineData("two words")]
    [InlineData("../software")]
    [InlineData("Software")]
    [InlineData("soft_ware")]
    public void A_field_whose_owner_is_not_a_module_name_is_refused(string owner)
    {
        ParcelType type = WithObjectiveOwnedBy(owner);

        DomainException exception = Assert.Throws<DomainException>(() => type.Validate(Gates));

        Assert.Equal(
            $"Field 'objective' of parcel type 'task' names '{owner}' as the module that owns it; a field's owner is a module's name: lowercase letters and digits in runs joined by single hyphens.",
            exception.Message);
    }

    private static ParcelType WithObjectiveOwnedBy(string owner) => SoftwareTypes.Task with
    {
        Fields = [.. SoftwareTypes.Task.Fields.Select(f => f.Name == "objective" ? f with { Owner = owner } : f)],
    };

    [Fact]
    public void The_shipped_task_declares_its_objective_criteria_non_goals_pull_request_and_repos()
    {
        Assert.Equal([("objective", FieldKinds.Text, "refine"), ("criteria", FieldKinds.TextList, "refine"), ("non-goals", FieldKinds.TextList, ""), ("pull-request", FieldKinds.Reference, "build"), ("repos", FieldKinds.TextList, "")], SoftwareTypes.Task.Fields.Select(f => (f.Name, f.Kind, string.Join(",", f.RequiredToLeave))));
        SoftwareTypes.Task.Validate(Gates);
    }

    [Fact]
    public void Fields_survive_the_canonical_json_and_are_part_of_the_hash()
    {
        ParcelType read = ParcelType.FromJson(ParcelType.CanonicalJson(SoftwareTypes.Task));

        Assert.Equal(SoftwareTypes.Task.Fields.Select(f => (f.Name, f.Kind)), read.Fields.Select(f => (f.Name, f.Kind)));
        Assert.Equal(SoftwareTypes.Task.Fields.Select(f => f.RequiredToLeave), read.Fields.Select(f => f.RequiredToLeave));
        Assert.Equal(SoftwareTypes.Task.Hash, read.Hash);
        string actual = ParcelType.CanonicalJson(SoftwareTypes.Task);
        Assert.Contains("\"Fields\"", actual, StringComparison.Ordinal);
        Assert.Contains("\"RequiredToLeave\"", actual, StringComparison.Ordinal);

        ParcelType renamed = SoftwareTypes.Task with
        {
            Fields = [.. SoftwareTypes.Task.Fields.Select(f => f.Name == "objective" ? f with { Name = "goal" } : f)],
        };
        ParcelType relaxed = SoftwareTypes.Task with
        {
            Fields = [.. SoftwareTypes.Task.Fields.Select(f => f.Name == "objective" ? f with { RequiredToLeave = [] } : f)],
        };
        ParcelType rekinded = SoftwareTypes.Task with
        {
            Fields = [.. SoftwareTypes.Task.Fields.Select(f => f.Name == "objective" ? f with { Kind = FieldKinds.TextList } : f)],
        };
        string[] hashes = [SoftwareTypes.Task.Hash, renamed.Hash, relaxed.Hash, rekinded.Hash, (SoftwareTypes.Task with { Fields = [] }).Hash];
        Assert.Distinct(hashes);
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
        Assert.False(string.IsNullOrEmpty(broken));
        ParcelType type = SoftwareTypes.Task with { Fields = fields };

        DomainException exception = Assert.Throws<DomainException>(() => type.Validate(Gates));
        Assert.Matches(WildcardPattern.ToRegex(rule), exception.Message);
    }

    [Theory]
    [InlineData("[null]", "Parcel type 'task' has a field that is null;*")]
    [InlineData("""[{"Name": "objective", "Kind": "text", "RequiredToLeave": [null]}]""", "*required to leave ''*")]
    public void A_read_field_that_is_null_or_names_a_null_stage_is_refused(string fields, string rule)
    {
        JsonObject content = JsonNode.Parse(ParcelType.CanonicalJson(SoftwareTypes.Task))?.AsObject()
            ?? throw new InvalidOperationException("A type's canonical content is a JSON object.");
        content["Fields"] = JsonNode.Parse(fields);
        string replaced = content.ToJsonString();

        ParcelType read = ParcelType.FromJson(replaced);

        DomainException exception = Assert.Throws<DomainException>(() => read.Validate(Gates));
        Assert.Matches(WildcardPattern.ToRegex(rule), exception.Message);
    }

    [Fact]
    public void The_shipped_types_enforce_every_field_they_declare()
    {
        SoftwareTypes.Idea.Validate(Gates);
        SoftwareTypes.Task.Validate(Gates);
    }

    [Fact]
    public void A_type_that_declares_a_field_required_to_leave_a_stage_with_a_move_out_not_bound_to_the_field_gate_is_refused_naming_the_field_and_the_move()
    {
        ParcelType unbound = SoftwareTypes.Task with
        {
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build", "review"],
                ["build"] = ["review"],
                ["review"] = ["build"],
            },
        };

        DomainException exception = Assert.Throws<DomainException>(() => unbound.Validate(Gates));
        Assert.Equal("Field 'objective' of parcel type 'task' is required to leave 'refine', and the move from 'refine' to 'review' is not bound to the 'required-fields' gate; every move the table holds out of a stage a field is required to leave is bound to that gate.", exception.Message);
    }

    [Fact]
    public void A_table_entry_out_of_a_required_stage_into_paused_or_closed_needs_no_field_gate_since_pausing_and_closing_are_never_refused_for_an_empty_field()
    {
        ParcelType pausesInTable = SoftwareTypes.Task with
        {
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build", SoftwareTypes.Paused, SoftwareTypes.Closed],
                ["build"] = ["review"],
                ["review"] = ["build"],
            },
        };

        pausesInTable.Validate(Gates);
    }

    [Fact]
    public void A_type_with_a_stage_some_field_is_required_to_enter_and_no_move_into_it_bound_to_the_field_gate_is_refused_naming_the_stage_and_the_field()
    {
        ParcelType withQa = SoftwareTypes.Task with
        {
            Stages = [.. SoftwareTypes.Task.Stages, "qa"],
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build"],
                ["build"] = ["review"],
                ["review"] = ["build", "qa"],
            },
        };

        DomainException exception = Assert.Throws<DomainException>(() => withQa.Validate(Gates));
        Assert.Equal("Stage 'qa' of parcel type 'task' requires 'objective', 'criteria', 'pull-request' to enter, and no move the table holds into it is bound to the 'required-fields' gate, so a resume into it would check nothing; a stage some field is required to enter has a move into it bound to that gate.", exception.Message);

        ParcelType guarded = withQa with
        {
            GateBindings = [.. withQa.GateBindings, new GateBinding("review", "qa", RequiredFieldsGate.GateName)],
        };
        guarded.Validate(Gates);
        Assert.Equal(["objective", "criteria", "pull-request"], guarded.RequiredToEnter("qa"));
    }

    [Fact]
    public void A_field_is_required_to_enter_a_stage_only_when_every_route_to_it_requires_it_and_never_for_a_stage_no_route_reaches()
    {
        ParcelType forked = SoftwareTypes.Task with
        {
            Stages = ["refine", "spike", "build", "review", "shelved", SoftwareTypes.Paused, SoftwareTypes.Closed],
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build"],
                ["spike"] = ["build"],
                ["build"] = ["review"],
                ["review"] = ["build"],
            },
            EntryStages = ["refine", "spike"],
        };

        // a route from the spike entry stage reaches build without leaving refine
        Assert.Empty(forked.RequiredToEnter("build"));
        Assert.Equal(["pull-request"], forked.RequiredToEnter("review"));
        // no route reaches it
        Assert.Empty(forked.RequiredToEnter("shelved"));
    }

    [Fact]
    public void A_move_out_of_a_stage_no_route_reaches_into_a_stage_some_field_is_required_to_enter_is_refused_naming_both()
    {
        ParcelType shelved = SoftwareTypes.Task with
        {
            Stages = ["refine", "build", "review", "shelved", SoftwareTypes.Paused, SoftwareTypes.Closed],
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build"],
                ["build"] = ["review"],
                ["review"] = ["build"],
                ["shelved"] = ["review"],
            },
        };

        DomainException exception = Assert.Throws<DomainException>(() => shelved.Validate(Gates));
        Assert.Equal("Stage 'review' of parcel type 'task' requires 'objective', 'criteria', 'pull-request' to enter, and the move the table holds into it from 'shelved', a stage no route from an entry stage reaches, would carry a parcel resumed into 'shelved' there without them; a move out of a stage no route reaches goes only into a stage that requires nothing to enter.", exception.Message);

        ParcelType intoRefine = shelved with
        {
            Moves = new Dictionary<string, IReadOnlyList<string>>(shelved.Moves) { ["shelved"] = ["refine"] },
        };
        // refine requires nothing to enter
        intoRefine.Validate(Gates);
    }

    [Fact]
    public void Drafting_or_creating_a_parcel_under_a_type_that_leaves_a_field_unenforced_is_refused()
    {
        ParcelType unenforced = SoftwareTypes.Task with
        {
            GateBindings = [new GateBinding("refine", "build", RequiredFieldsGate.GateName)],
        };
        string content = ParcelType.CanonicalJson(unenforced);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0,
                content, unenforced.Hash, null, "brian", DateTimeOffset.UnixEpoch, Gates));
        Assert.StartsWith("Field 'pull-request' of parcel type 'task' is required to leave 'build', and the move from 'build' to 'review' is not bound", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(() => ParcelDecider.Create(unenforced, GateSet.Shipped, "alpha", "Work", null, "brian", DateTimeOffset.UnixEpoch));
        Assert.Matches(@"(?s)^Field 'pull-request' of parcel type 'task'.*not bound.*$", exception2.Message);
    }

    private static FieldDeclaration Text(string name, params string[] requiredToLeave) =>
        new() { Name = name, Kind = FieldKinds.Text, RequiredToLeave = requiredToLeave };
}
