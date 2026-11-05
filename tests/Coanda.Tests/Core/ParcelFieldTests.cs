using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

/// <summary>Setting a parcel's fields, and the required-fields gate the shipped task binds to its moves out of refine and out of build.</summary>
public sealed class ParcelFieldTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly ParcelType Task = SoftwareTypes.Task;
    private static readonly GateSet Gates = GateSet.Shipped;

    private static Parcel NewTask() => Parcel.Create(ParcelDecider.Create(Task, Gates, "coanda", "Fields", null, "brian", Now));

    private static Parcel Set(Parcel parcel, string field, FieldValue value)
    {
        parcel.Apply(ParcelDecider.SetField(Task, parcel, field, value, "brian", Now));
        return parcel;
    }

    private static Parcel Moved(Parcel parcel, string to)
    {
        parcel.Apply(ParcelDecider.Move(Task, Gates, parcel, to, "brian", Now));
        return parcel;
    }

    // Setting a field.

    [Fact]
    public void Each_kind_is_recorded_as_an_event_and_a_second_set_replaces_the_value_whole()
    {
        Parcel parcel = NewTask();

        ParcelFieldSet text = ParcelDecider.SetField(Task, parcel, "objective", FieldValue.OfText("Ship fields."), "brian", Now);
        parcel.Apply(text);
        Set(parcel, "criteria", FieldValue.OfItems("one", "two"));
        Set(parcel, "criteria", FieldValue.OfItems("three"));
        Set(parcel, "pull-request", FieldValue.OfReference("42", "https://example.com/pull/42"));

        Assert.Equivalent(new { ParcelId = parcel.Id, Field = "objective", SetBy = "brian", SetAt = Now }, text);
        Assert.Equivalent(FieldValue.OfText("Ship fields."), parcel.ValueOf("objective"), strict: true);
        // a set replaces the list, it never appends to it
        Assert.Equivalent(FieldValue.OfItems("three"), parcel.ValueOf("criteria"), strict: true);
        Assert.Equivalent(FieldValue.OfReference("42", "https://example.com/pull/42"), parcel.ValueOf("pull-request"), strict: true);
        Assert.Null(parcel.ValueOf("non-goals"));
        Assert.Equal(Now, parcel.UpdatedAt);
    }

    [Fact]
    public void A_reference_takes_no_url_and_a_blank_url_is_recorded_as_none()
    {
        Assert.Equal(new FieldReference("42", null), ParcelDecider.SetField(Task, NewTask(), "pull-request", FieldValue.OfReference("42"), "brian", Now)
            .Value.Reference);
        Assert.Equal(new FieldReference("42", null), ParcelDecider.SetField(Task, NewTask(), "pull-request", FieldValue.OfReference("42", "  "), "brian", Now)
            .Value.Reference);
    }

    [Fact]
    public void A_field_the_type_version_does_not_declare_is_refused_quoting_the_rule()
    {
        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.SetField(Task, NewTask(), "branch", FieldValue.OfText("main"), "brian", Now));
        Assert.Equal("Parcel type 'task' declares no field 'branch'; a parcel holds values only in the fields its type declares: objective, criteria, non-goals, pull-request.", exception.Message);

        Parcel idea = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Idea, Gates, "coanda", "No fields", null, "brian", Now));
        DomainException exception2 = Assert.Throws<DomainException>(() => ParcelDecider.SetField(SoftwareTypes.Idea, idea, "objective", FieldValue.OfText("x"), "brian", Now));
        Assert.Matches(@"(?s)^.*declares no field 'objective'.*and it declares none\.$", exception2.Message);
    }

    public static TheoryData<string, FieldValue, string> WrongKind => new()
    {
        { "objective", FieldValue.OfItems("a list"), "Field 'objective' of a 'task' holds a text, and the value given is a list of text; a field takes only a value of its own kind." },
        { "criteria", FieldValue.OfText("one line"), "Field 'criteria' of a 'task' holds a list of text, and the value given is a text;*" },
        { "pull-request", FieldValue.OfText("42"), "Field 'pull-request' of a 'task' holds a reference, and the value given is a text;*" },
        { "objective", new FieldValue { Text = "x", Items = ["y"] }, "Field 'objective' of a 'task' holds a text, and the value given is not one kind of value;*" },
        { "objective", new FieldValue(), "*the value given is not one kind of value;*" },
    };

    [Theory]
    [MemberData(nameof(WrongKind))]
    public void A_value_of_the_wrong_kind_is_refused_quoting_the_rule(string field, FieldValue value, string rule)
    {
        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.SetField(Task, NewTask(), field, value, "brian", Now));
        Assert.Matches(WildcardPattern.ToRegex(rule), exception.Message);
    }

    public static TheoryData<string, FieldValue> Empty => new()
    {
        { "objective", FieldValue.OfText("") },
        { "objective", FieldValue.OfText(" \t\n ") },
        { "criteria", FieldValue.OfItems() },
        { "criteria", FieldValue.OfItems("fine", " ") },
        { "pull-request", FieldValue.OfReference("", "https://example.com/pull/42") },
        { "pull-request", FieldValue.OfReference("   ") },
    };

    [Theory]
    [MemberData(nameof(Empty))]
    public void An_empty_value_is_refused_quoting_the_rule(string field, FieldValue value)
    {
        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.SetField(Task, NewTask(), field, value, "brian", Now));
        Assert.Equal($"The value given for field '{field}' is empty; a field takes no empty value: no text that is empty or only white space, no list without items or with an empty item, and no reference without an identifier.", exception.Message);
    }

    [Fact]
    public void A_closed_parcel_is_refused_quoting_the_rule()
    {
        Parcel parcel = NewTask();
        parcel.Apply(ParcelDecider.Close(Task, Gates, parcel, "abandoned", "Not wanted.", "brian", Now));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.SetField(Task, parcel, "objective", FieldValue.OfText("Too late."), "brian", Now));
        Assert.Equal($"Parcel {Ids.Short(parcel.Id)} is closed (abandoned); a closed parcel's fields are set by nobody.", exception.Message);
    }

    [Fact]
    public void A_parcel_of_another_type_version_is_refused()
    {
        ParcelType older = Task with { Fields = [.. Task.Fields.Where(f => f.Name != "non-goals")] };
        Parcel parcel = Parcel.Create(ParcelDecider.Create(older, Gates, "coanda", "Older", null, "brian", Now));

        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.SetField(Task, parcel, "non-goals", FieldValue.OfItems("x"), "brian", Now));
        Assert.Contains("was created under 'task@", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(() => ParcelDecider.SetField(older, parcel, "non-goals", FieldValue.OfItems("x"), "brian", Now));
        Assert.Contains("declares no field 'non-goals'", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parcel_snapshot_written_before_fields_existed_reads_with_no_values_set()
    {
        Parcel parcel = NewTask();
        string snapshot = JsonSerializer.Serialize(parcel);
        Assert.Contains("\"Fields\":{}", snapshot, StringComparison.Ordinal);

        Parcel read = JsonSerializer.Deserialize<Parcel>(snapshot.Replace(",\"Fields\":{}", string.Empty, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The snapshot read as nothing.");

        Assert.Equal(parcel.Id, read.Id);
        Assert.Empty(read.Fields);
        Assert.Null(read.ValueOf("objective"));
    }

    // The required-fields gate.

    [Fact]
    public void The_move_from_refine_to_build_is_refused_naming_every_required_field_without_a_value()
    {
        Parcel parcel = NewTask();

        DomainException exception = Assert.Throws<DomainException>(() => Moved(parcel, "build"));
        Assert.Equal("The gate 'required-fields' refused the move from 'refine' to 'build': a 'task' leaves 'refine' only with a value in every field required to leave it, and 'objective', 'criteria' have none.", exception.Message);

        Set(parcel, "objective", TaskFields.ObjectiveValue);
        DomainException exception2 = Assert.Throws<DomainException>(() => Moved(parcel, "build"));
        Assert.EndsWith("and 'criteria' has none.", exception2.Message, StringComparison.Ordinal);

        Set(parcel, "criteria", TaskFields.CriteriaValue);
        // non-goals is required to leave no stage
        Assert.Equal("build", Moved(parcel, "build").Stage);
    }

    [Fact]
    public void The_move_from_build_to_review_is_refused_while_pull_request_has_no_value()
    {
        Parcel parcel = Moved(NewTask().Briefed(Task, pullRequest: false), "build");

        DomainException exception = Assert.Throws<DomainException>(() => Moved(parcel, "review"));
        Assert.Equal("The gate 'required-fields' refused the move from 'build' to 'review': a 'task' leaves 'build' only with a value in every field required to leave it, and 'pull-request' has none.", exception.Message);

        Set(parcel, "pull-request", TaskFields.PullRequestValue);
        Assert.Equal("review", Moved(parcel, "review").Stage);
        // no gate is bound to the move from review back to build
        Assert.Equal("build", Moved(parcel, "build").Stage);
    }

    [Fact]
    public void The_shipped_task_requires_nothing_to_enter_refine_the_brief_to_enter_build_and_the_brief_and_pull_request_to_enter_review()
    {
        Assert.Empty(Task.RequiredToEnter("refine"));
        Assert.Equal(["objective", "criteria"], Task.RequiredToEnter("build"));
        Assert.Equal(["objective", "criteria", "pull-request"], Task.RequiredToEnter("review"));
        Assert.Empty(Task.RequiredToEnter("paused"));
        Assert.Empty(Task.RequiredToEnter("closed"));
    }

    /// <summary>
    /// A task standing paused after <paramref name="pausedFrom"/>, reached by recording the moves
    /// directly, the way a stored parcel's events replay, so the gates the decider runs on the way do
    /// not decide which fields it holds.
    /// </summary>
    private static Parcel PausedFrom(string pausedFrom, params string[] fields)
    {
        Parcel parcel = NewTask();
        foreach (string field in fields)
        {
            Set(parcel, field, field switch
            {
                TaskFields.Objective => TaskFields.ObjectiveValue,
                TaskFields.Criteria => TaskFields.CriteriaValue,
                _ => TaskFields.PullRequestValue,
            });
        }

        if (pausedFrom == "build")
        {
            parcel.Apply(new ParcelMoved(parcel.Id, "refine", "build", "brian", Now));
        }

        parcel.Apply(new ParcelMoved(parcel.Id, pausedFrom, "paused", "brian", Now));
        return parcel;
    }

    [Fact]
    public void A_task_paused_from_refine_with_only_a_pull_request_is_refused_on_resuming_to_review_naming_the_objective_and_criteria()
    {
        Parcel parcel = PausedFrom("refine", TaskFields.PullRequest);

        DomainException exception = Assert.Throws<DomainException>(() => Moved(parcel, "review"));
        Assert.Equal("The gate 'required-fields' refused the move from 'paused' to 'review': a 'task' resumes into 'review' only with a value in every field required to enter it, which every route into it would have required, and in every field required to leave 'build', and 'objective', 'criteria' have none.", exception.Message);
        Assert.Equal("paused", parcel.Stage);
    }

    [Fact]
    public void A_task_paused_from_refine_with_the_objective_and_criteria_and_no_pull_request_resumes_to_build()
    {
        Parcel parcel = PausedFrom("refine", TaskFields.Objective, TaskFields.Criteria);

        Assert.Equal("build", Moved(parcel, "build").Stage);
    }

    [Fact]
    public void A_task_paused_from_build_with_nothing_set_resumes_to_refine()
    {
        Parcel parcel = PausedFrom("build");

        Assert.Equal("refine", Moved(parcel, "refine").Stage);
    }

    [Fact]
    public void A_task_paused_from_build_with_all_three_fields_set_resumes_to_review()
    {
        Parcel parcel = PausedFrom("build", TaskFields.Objective, TaskFields.Criteria, TaskFields.PullRequest);

        Assert.Equal("review", Moved(parcel, "review").Stage);
    }

    [Fact]
    public void A_resume_into_build_names_every_field_required_to_enter_it_that_has_no_value()
    {
        Parcel parcel = Moved(NewTask(), "paused");

        DomainException exception = Assert.Throws<DomainException>(() => Moved(parcel, "build"));
        Assert.Matches(@"(?s)^The gate 'required-fields' refused the move from 'paused' to 'build': a 'task' resumes into 'build' only.*'objective', 'criteria' have none\.$", exception.Message);

        Set(parcel, "objective", TaskFields.ObjectiveValue);
        DomainException exception2 = Assert.Throws<DomainException>(() => Moved(parcel, "build"));
        Assert.Matches(@"(?s)^.*resumes into 'build'.*and 'criteria' has none\.$", exception2.Message);

        Set(parcel, "criteria", TaskFields.CriteriaValue);
        Assert.Equal("build", Moved(parcel, "build").Stage);
    }

    [Fact]
    public void A_resume_into_review_still_checks_what_the_bound_move_from_build_checks()
    {
        Parcel parcel = Moved(NewTask().Briefed(Task, pullRequest: false), "paused");

        DomainException exception = Assert.Throws<DomainException>(() => Moved(parcel, "review"));
        Assert.Matches(@"(?s)^The gate 'required-fields' refused the move from 'paused' to 'review': a 'task' resumes into 'review' only.*'pull-request' has none\.$", exception.Message);

        Set(parcel, "pull-request", TaskFields.PullRequestValue);
        Assert.Equal("review", Moved(parcel, "review").Stage);
    }

    [Fact]
    public void A_resume_into_a_stage_with_two_bound_entry_predecessors_still_checks_what_each_bound_move_checks()
    {
        ParcelType forked = Task with
        {
            Stages = ["refine", "spike", "build", "review", SoftwareTypes.Paused, SoftwareTypes.Closed],
            Moves = new Dictionary<string, IReadOnlyList<string>>
            {
                ["refine"] = ["build"],
                ["spike"] = ["build"],
                ["build"] = ["review"],
            },
            EntryStages = ["refine", "spike"],
            Fields = [.. Task.Fields, new FieldDeclaration { Name = "hypothesis", Kind = FieldKinds.Text, RequiredToLeave = ["spike"] }],
            GateBindings = [.. Task.GateBindings, new GateBinding("spike", "build", RequiredFieldsGate.GateName)],
        };
        forked.Validate(Gates.Names);
        Parcel parcel = Parcel.Create(ParcelDecider.Create(forked, Gates, "coanda", "Forked", "refine", "brian", Now));
        parcel.Apply(ParcelDecider.Move(forked, Gates, parcel, "paused", "brian", Now));

        // a route from either entry stage reaches build without leaving the other
        Assert.Empty(forked.RequiredToEnter("build"));
        DomainException exception = Assert.Throws<DomainException>(() => ParcelDecider.Move(forked, Gates, parcel, "build", "brian", Now));
        Assert.Matches(@"(?s)^The gate 'required-fields' refused the move from 'paused' to 'build': a 'task' resumes into 'build'.*required to leave 'refine', and 'objective', 'criteria' have none\.$", exception.Message);

        parcel.Apply(ParcelDecider.SetField(forked, parcel, TaskFields.Objective, TaskFields.ObjectiveValue, "brian", Now));
        parcel.Apply(ParcelDecider.SetField(forked, parcel, TaskFields.Criteria, TaskFields.CriteriaValue, "brian", Now));
        DomainException exception2 = Assert.Throws<DomainException>(() => ParcelDecider.Move(forked, Gates, parcel, "build", "brian", Now));
        Assert.Matches(@"(?s)^.*resumes into 'build'.*required to leave 'spike', and 'hypothesis' has none\.$", exception2.Message);

        parcel.Apply(ParcelDecider.SetField(forked, parcel, "hypothesis", FieldValue.OfText("It holds."), "brian", Now));
        Assert.Equal("build", ParcelDecider.Move(forked, Gates, parcel, "build", "brian", Now).ToStage);
    }

    [Fact]
    public void Pausing_or_closing_from_refine_or_build_is_never_refused_for_an_empty_field()
    {
        Assert.Equal("paused", Moved(NewTask(), "paused").Stage);
        Assert.Equal("closed", ParcelDecider.Close(Task, Gates, NewTask(), "abandoned", "Not wanted.", "brian", Now).TerminalStage);

        Parcel atBuild = Moved(NewTask().Briefed(Task, pullRequest: false), "build");
        Assert.Equal("paused", ParcelDecider.Move(Task, Gates, atBuild, "paused", "brian", Now).ToStage);
        Assert.Equal("closed", ParcelDecider.Close(Task, Gates, atBuild, "superseded", "Done elsewhere.", "brian", Now).TerminalStage);
        Moved(atBuild, "paused");
        Assert.Equal("closed", ParcelDecider.Close(Task, Gates, atBuild, "abandoned", "Not wanted.", "brian", Now).TerminalStage);
    }

    [Fact]
    public void The_gate_reads_the_declarations_of_any_type_it_is_given()
    {
        ParcelType chore = new()
        {
            Name = "chore",
            Stages = ["todo", "doing", "paused", "closed"],
            EntryStages = ["todo"],
            TerminalStage = "closed",
            Resolutions = ["done"],
            PausedStage = "paused",
            Moves = new Dictionary<string, IReadOnlyList<string>> { ["todo"] = ["doing"] },
            Fields = [new FieldDeclaration { Name = "owner", Kind = FieldKinds.Reference, RequiredToLeave = ["todo"] }],
            GateBindings = [new GateBinding("todo", "doing", RequiredFieldsGate.GateName)],
        };
        Parcel parcel = Parcel.Create(ParcelDecider.Create(chore, Gates, "coanda", "Sweep", null, "brian", Now));

        Assert.Equal(GateVerdict.Fail("a 'chore' leaves 'todo' only with a value in every field required to leave it, and 'owner' has none."), new RequiredFieldsGate().Check(parcel, chore, "todo", "doing"));
        // no field is required to leave doing
        Assert.Equal(GateVerdict.Pass, new RequiredFieldsGate().Check(parcel, chore, "doing", "todo"));
    }

    [Fact]
    public void The_shipped_gate_set_holds_the_required_fields_gate()
    {
        Assert.Equivalent(new[] { RequiredFieldsGate.GateName }, GateSet.Shipped.Names, strict: true);
        Assert.IsType<RequiredFieldsGate>(GateSet.Shipped.Find(RequiredFieldsGate.GateName));
        Assert.Empty(GateSet.None.Names);
    }

    [Fact]
    public void The_gate_source_names_no_field_and_nothing_of_one_lifecycle()
    {
        string source = File.ReadAllText(SourceFile("Parcels", "RequiredFieldsGate.cs"));

        foreach (string word in new[] { "objective", "criteria", "pull", "branch", "worktree", "non-goals", "refine", "build", "review" })
        {
            Assert.DoesNotContain(word, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string SourceFile(string folder, string file)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "src", "Coanda.Core")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new InvalidOperationException("The repository root was not found."), "src", "Coanda.Core", folder, file);
    }
}
