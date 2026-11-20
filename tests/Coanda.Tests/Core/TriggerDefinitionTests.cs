using Coanda.Core;
using Coanda.Core.Triggers;

namespace Coanda.Tests.Core;

/// <summary>
/// A trigger definition's content: its canonical form and hash, and the rules on its event kind, its
/// filters, its target, and its hourly cap.
/// </summary>
public sealed class TriggerDefinitionTests
{
    private static string Refusal(TriggerDefinition trigger) => Assert.Throws<DomainException>(trigger.Validate).Message;

    [Fact]
    public void The_sample_triggers_are_valid_and_read_back_from_their_canonical_json_unchanged()
    {
        foreach (TriggerDefinition trigger in new[] { SampleTriggers.IssueOpened, SampleTriggers.ReviewRequested, SampleTriggers.EvaluateBuild })
        {
            trigger.Validate();
            string canonical = TriggerDefinition.CanonicalJson(trigger);
            TriggerDefinition read = TriggerDefinition.FromJson(canonical);

            Assert.Equal(trigger.Hash, read.Hash);
            Assert.Equal(canonical, TriggerDefinition.CanonicalJson(read));
        }
    }

    [Fact]
    public void Two_layouts_differing_in_whitespace_member_order_and_filter_order_read_to_one_canonical_form_and_hash()
    {
        const string first = """
            {"Name":"issue-opened","EventKind":"tracker/issue-opened","Filters":{"repo":"coanda","assignee":"pat"},
             "Target":{"CreateParcel":{"ParcelType":"issue","TitleField":"title","Flow":"triage"}},"HourlyCap":10}
            """;
        const string second = """
            {
              "HourlyCap": 10,
              "Target": { "CreateParcel": { "Flow": "triage", "TitleField": "title", "ParcelType": "issue" } },
              "Filters": { "assignee": "pat", "repo": "coanda" },
              "EventKind": "tracker/issue-opened",
              "Name": "issue-opened"
            }
            """;

        TriggerDefinition one = TriggerDefinition.FromJson(first);
        TriggerDefinition other = TriggerDefinition.FromJson(second);

        Assert.Equal(TriggerDefinition.CanonicalJson(one), TriggerDefinition.CanonicalJson(other));
        Assert.Equal(one.Hash, other.Hash);
        Assert.Equal(SampleTriggers.IssueOpened.Hash, one.Hash);
        Assert.Equal(TriggerDefinition.CanonicalJson(SampleTriggers.IssueOpened), TriggerDefinition.CanonicalJson(one));
    }

    [Fact]
    public void The_canonical_form_writes_the_filters_in_ordinal_order_of_their_field_names()
    {
        string canonical = TriggerDefinition.CanonicalJson(SampleTriggers.IssueOpened);

        Assert.True(
            canonical.IndexOf("\"assignee\"", StringComparison.Ordinal) < canonical.IndexOf("\"repo\"", StringComparison.Ordinal),
            canonical);
    }

    [Fact]
    public void Changing_any_one_member_changes_the_hash()
    {
        TriggerDefinition trigger = SampleTriggers.IssueOpened;
        CreateParcelTarget create = trigger.Target.CreateParcel ?? throw new InvalidOperationException("The sample creates a parcel.");
        TriggerDefinition[] changed =
        [
            trigger with { Name = "issue-filed" },
            trigger with { EventKind = "tracker/issue-filed" },
            trigger with { Filters = SampleTriggers.Filters(("repo", "coanda")) },
            trigger with { Filters = SampleTriggers.Filters(("repo", "coanda"), ("assignee", "sam")) },
            trigger with { Filters = SampleTriggers.Filters(("repo", "coanda"), ("owner", "pat")) },
            trigger with { Target = new TriggerTarget { CreateParcel = create with { ParcelType = "bug" } } },
            trigger with { Target = new TriggerTarget { CreateParcel = create with { TitleField = "summary" } } },
            trigger with { Target = new TriggerTarget { CreateParcel = create with { Flow = "refine" } } },
            trigger with { Target = new TriggerTarget { EventParcel = new EventParcelTarget { Flow = "triage" } } },
            trigger with { HourlyCap = 11 },
            trigger with { HourlyCap = null },
        ];

        string[] hashes = [trigger.Hash, .. changed.Select(t => t.Hash)];
        Assert.Equal(hashes.Length, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"Name":"x","EventKind":"opened","Target":null}""")]
    [InlineData("""{"Name":"x","EventKind":"opened","Target":{"EventParcel":{"Flow":"f"}},"Schedule":"hourly"}""")]
    [InlineData("""{"Name":"x","EventKind":"opened","Filters":{"a":"1","a":"2"},"Target":{"EventParcel":{"Flow":"f"}}}""")]
    [InlineData("""{"Name":"x","EventKind":"opened","Target":{"CreateParcel":{"ParcelType":"t","TitleField":"f","Flow":"g"}},"HourlyCap":1.5}""")]
    public void Content_that_cannot_be_read_as_a_trigger_is_refused(string content)
    {
        DomainException exception = Assert.Throws<DomainException>(() => TriggerDefinition.FromJson(content));

        Assert.Matches(@"(?s)^The content.*trigger definition.*$", exception.Message);
    }

    [Theory]
    [InlineData("opened")]
    [InlineData("issue_opened-2")]
    [InlineData("tracker/issue-opened")]
    [InlineData(TriggerEvent.FlowRunEndedKind)]
    public void A_plain_a_module_prefixed_or_a_flow_run_end_kind_is_accepted(string kind)
    {
        TriggerDefinition trigger = kind == TriggerEvent.FlowRunEndedKind
            ? SampleTriggers.EvaluateBuild
            : SampleTriggers.IssueOpened with { EventKind = kind };

        trigger.Validate();
    }

    [Theory]
    [InlineData("issue opened")]
    [InlineData("Tracker/opened")]
    [InlineData("tracker/")]
    [InlineData("tracker/issue/opened")]
    [InlineData("")]
    public void A_kind_that_is_neither_plain_nor_module_prefixed_is_refused(string kind)
    {
        string message = Refusal(SampleTriggers.IssueOpened with { EventKind = kind });

        Assert.Equal(
            $"Trigger 'issue-opened' needs an event kind that is a plain name (letters, digits, hyphens, and underscores), a module's name and a plain name joined by a slash (<module>/<name>), where a module's name is lowercase letters and digits in runs joined by single hyphens, or 'flow-run-ended'; '{kind}' is not.",
            message);
    }

    [Fact]
    public void A_trigger_on_a_parcel_entering_a_stage_is_refused_as_a_types_flow_binding()
    {
        string message = Refusal(SampleTriggers.IssueOpened with { EventKind = TriggerEvent.ParcelEnteredKind });

        Assert.Equal(
            "Trigger 'issue-opened' triggers on 'parcel-entered', which a parcel's own creation and moves read as; a parcel entering a stage starts a flow through its type's flow binding, not through a trigger.",
            message);
    }

    [Theory]
    [InlineData("assignee.login")]
    [InlineData("assignee login")]
    [InlineData("")]
    public void A_filter_whose_field_name_is_not_plain_is_refused(string field)
    {
        string message = Refusal(SampleTriggers.IssueOpened with { Filters = SampleTriggers.Filters(("repo", "coanda"), (field, "pat")) });

        Assert.Equal(
            $"Trigger 'issue-opened' filters on the field '{field}'; a filter names an event field, which is a plain name (letters, digits, hyphens, and underscores).",
            message);
    }

    [Fact]
    public void A_trigger_with_no_filters_is_accepted()
    {
        (SampleTriggers.IssueOpened with { Filters = SampleTriggers.Filters() }).Validate();
    }

    public static TheoryData<TriggerTarget, string> BrokenTargets() => new()
    {
        { new TriggerTarget(), "Trigger 'issue-opened' has no target; a trigger's target either creates a parcel or acts on the parcel the event names." },
        {
            new TriggerTarget
            {
                CreateParcel = new CreateParcelTarget { ParcelType = "issue", TitleField = "title", Flow = "triage" },
                EventParcel = new EventParcelTarget { Flow = "triage" },
            },
            "Trigger 'issue-opened' has a target that both creates a parcel and acts on the parcel the event names; a target does one or the other."
        },
        {
            new TriggerTarget { CreateParcel = new CreateParcelTarget { TitleField = "title", Flow = "triage" } },
            "Trigger 'issue-opened' has a target that names no parcel type; its target names the parcel type it creates."
        },
        {
            new TriggerTarget { CreateParcel = new CreateParcelTarget { ParcelType = "issue", TitleField = " ", Flow = "triage" } },
            "Trigger 'issue-opened' has a target that names no title field; its target names the event field whose value titles the parcel it creates."
        },
        {
            new TriggerTarget { CreateParcel = new CreateParcelTarget { ParcelType = "issue", TitleField = "title" } },
            "Trigger 'issue-opened' has a target that names no flow; its target names the flow it starts on the parcel it creates."
        },
        {
            new TriggerTarget { CreateParcel = new CreateParcelTarget { ParcelType = "issue", TitleField = "issue.title", Flow = "triage" } },
            "Trigger 'issue-opened' titles the parcel it creates with the field 'issue.title'; an event field is a plain name (letters, digits, hyphens, and underscores)."
        },
        {
            new TriggerTarget { EventParcel = new EventParcelTarget() },
            "Trigger 'issue-opened' has a target that names no flow; its target names the flow it starts on the parcel the event names."
        },
    };

    [Theory]
    [MemberData(nameof(BrokenTargets))]
    public void A_target_that_is_neither_both_or_lacks_a_name_it_needs_is_refused_saying_which(TriggerTarget target, string expected)
    {
        Assert.Equal(expected, Refusal(SampleTriggers.IssueOpened with { Target = target }));
    }

    [Theory]
    [InlineData(null, "carries no hourly cap")]
    [InlineData(0, "carries the hourly cap 0")]
    [InlineData(-3, "carries the hourly cap -3")]
    public void A_trigger_that_creates_parcels_without_a_cap_of_at_least_one_is_refused(int? cap, string carries)
    {
        string message = Refusal(SampleTriggers.IssueOpened with { HourlyCap = cap });

        Assert.Equal(
            $"Trigger 'issue-opened' creates parcels and {carries}; a trigger that creates parcels carries an hourly cap that is a whole number of at least one.",
            message);
    }

    [Fact]
    public void A_cap_of_one_is_accepted()
    {
        (SampleTriggers.IssueOpened with { HourlyCap = 1 }).Validate();
    }

    [Fact]
    public void A_trigger_that_acts_on_the_events_parcel_is_refused_when_it_carries_a_cap()
    {
        string message = Refusal(SampleTriggers.ReviewRequested with { HourlyCap = 4 });

        Assert.Equal(
            "Trigger 'review-requested' acts on the parcel the event names and carries the hourly cap 4; it creates no parcel for a cap to count, so it carries none.",
            message);
    }

    [Fact]
    public void A_trigger_on_a_flow_run_end_may_leave_out_the_outcome()
    {
        (SampleTriggers.EvaluateBuild with { Filters = SampleTriggers.Filters(("flow", "build")) }).Validate();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("outcome", "pr-opened")]
    [InlineData("flow", " ")]
    public void A_trigger_on_a_flow_run_end_with_no_flow_filter_is_refused(string? field, string? value)
    {
        IReadOnlyDictionary<string, string> filters = field is null || value is null
            ? SampleTriggers.Filters()
            : SampleTriggers.Filters((field, value));

        string message = Refusal(SampleTriggers.EvaluateBuild with { Filters = filters });

        Assert.Equal(
            "Trigger 'evaluate-build' triggers on 'flow-run-ended' with no 'flow' filter; a trigger on a flow run's end names the flow whose end it follows with a filter on the event's 'flow' field.",
            message);
    }

    [Fact]
    public void A_trigger_on_a_flow_run_end_that_acts_on_the_events_parcel_is_refused_as_a_flows_follow()
    {
        string message = Refusal(SampleTriggers.EvaluateBuild with
        {
            Target = new TriggerTarget { EventParcel = new EventParcelTarget { Flow = "judge" } },
            HourlyCap = null,
        });

        Assert.Equal(
            "Trigger 'evaluate-build' triggers on 'flow-run-ended' and acts on the parcel whose flow run ended; a trigger on a flow run's end creates a parcel, and starting a flow on the same parcel is a flow's follow.",
            message);
    }

    [Fact]
    public void A_trigger_on_attention_that_acts_on_the_events_parcel_is_refused_since_the_parcel_holds_its_paused_flow_run()
    {
        string message = Refusal(new TriggerDefinition
        {
            Name = "chase",
            EventKind = "attention",
            Target = new TriggerTarget { EventParcel = new EventParcelTarget { Flow = "judge" } },
        });

        Assert.Equal(
            "Trigger 'chase' triggers on 'attention' and acts on the paused flow run's parcel; a pause's parcel still holds its paused flow run, so no flow can start on it, and a trigger on 'attention' creates a parcel.",
            message);
    }

    [Fact]
    public void A_trigger_on_attention_that_creates_a_parcel_is_valid()
    {
        new TriggerDefinition
        {
            Name = "chase",
            EventKind = "attention",
            Filters = SampleTriggers.Filters(("flow", "build")),
            Target = new TriggerTarget { CreateParcel = new CreateParcelTarget { ParcelType = "task", TitleField = "title", Flow = "judge" } },
            HourlyCap = 5,
        }.Validate();
    }

    [Theory]
    [InlineData("build", "build")]
    [InlineData("Build", "build")]
    [InlineData("build", "BUILD")]
    public void A_trigger_on_a_flow_run_end_that_starts_the_flow_it_follows_is_refused_without_regard_to_case(string followed, string started)
    {
        TriggerDefinition trigger = SampleTriggers.EvaluateBuild with
        {
            Filters = SampleTriggers.Filters(("flow", followed)),
            Target = new TriggerTarget { CreateParcel = new CreateParcelTarget { ParcelType = "evaluation", TitleField = "flow", Flow = started } },
        };

        Assert.Equal(
            $"Trigger 'evaluate-build' follows the end of the flow '{followed}' and starts the flow '{started}', the same flow; a trigger on a flow run's end starts a flow other than the one whose end it follows.",
            Refusal(trigger));
    }

    [Fact]
    public void A_flow_filter_is_stored_as_written()
    {
        TriggerDefinition trigger = SampleTriggers.EvaluateBuild with { Filters = SampleTriggers.Filters(("flow", "Build")) };

        Assert.Equal("Build", TriggerDefinition.FromJson(TriggerDefinition.CanonicalJson(trigger)).Filters["flow"]);
    }
}
