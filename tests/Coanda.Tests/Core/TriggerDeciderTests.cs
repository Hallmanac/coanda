using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;

namespace Coanda.Tests.Core;

public sealed class TriggerDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Principal Member = new("teammate", Role.Member);
    private static readonly Principal Owner = new("brian", Role.Owner);
    private static readonly Dictionary<string, string> NoFields = [];

    private static ExternalEventRecorded Record(string kind, Dictionary<string, string>? fields = null, Principal? recordedBy = null) =>
        TriggerDecider.RecordExternalEvent("alpha", kind, "delivery-1", null, fields ?? NoFields, recordedBy ?? Owner, Now);

    [Fact]
    public void An_external_event_records_its_kind_and_fields()
    {
        ExternalEventRecorded recorded = Record("pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian", ["pr_number"] = "12" });

        Assert.Equal("alpha", recorded.Project);
        Assert.Equal("pr-assigned", recorded.Kind);
        Assert.Equivalent(new Dictionary<string, string> { ["assignee"] = "brian", ["pr_number"] = "12" }, recorded.Fields, strict: true);
        Assert.Equal("brian", recorded.RecordedBy);
    }

    [Fact]
    public void An_external_event_records_its_external_id_exactly_as_given_and_the_parcel_id_its_recorder_names()
    {
        Guid parcel = Ids.New();

        ExternalEventRecorded named = TriggerDecider.RecordExternalEvent("alpha", "pr-assigned", " Node-ID ", parcel, NoFields, Owner, Now);
        ExternalEventRecorded unnamed = TriggerDecider.RecordExternalEvent("alpha", "pr-assigned", "delivery-2", null, NoFields, Owner, Now);

        Assert.Equal((" Node-ID ", (Guid?)parcel), (named.ExternalId, named.ParcelId));
        Assert.Equal(("delivery-2", (Guid?)null), (unnamed.ExternalId, unnamed.ParcelId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void An_external_event_with_a_missing_or_blank_external_id_is_refused(string? externalId)
    {
        DomainException exception = Assert.Throws<DomainException>(
            () => TriggerDecider.RecordExternalEvent("alpha", "pr-assigned", externalId, null, NoFields, Owner, Now));

        Assert.Equal($"{TriggerDecider.ExternalIdRule}; none was given.", exception.Message);
    }

    [Fact]
    public void An_external_id_at_the_cap_is_recorded_and_one_a_character_over_it_is_refused()
    {
        string atCap = new('9', ExternalEventCaps.ExternalIdLength);

        Assert.Equal(atCap, TriggerDecider.RecordExternalEvent("alpha", "pr-assigned", atCap, null, NoFields, Owner, Now).ExternalId);
        DomainException exception = Assert.Throws<DomainException>(
            () => TriggerDecider.RecordExternalEvent("alpha", "pr-assigned", atCap + "9", null, NoFields, Owner, Now));
        Assert.Equal(
            $"An external event's external id is at most {ExternalEventCaps.ExternalIdLength} characters; this one is {ExternalEventCaps.ExternalIdLength + 1}.",
            exception.Message);
    }

    [Fact]
    public void The_external_id_cap_holds_a_code_hosts_ids_well_inside_it()
    {
        // A GitHub GraphQL node id, a webhook delivery id, and a schedule tick's fixed id, at a tenth of the cap or less.
        string[] ids = ["PR_kwDOLx1Q0M5vZ8nT", "MDExOlB1bGxSZXF1ZXN0MjA4NjE5MDQ0OQ==", "72d3162e-cc78-11e3-81ab-4c9367dc0958", "nightly-sweep@2026-11-18T09:00:00Z"];

        Assert.All(ids, id => Assert.True(id.Length * 10 <= ExternalEventCaps.ExternalIdLength, id));
    }

    [Fact]
    public void An_external_event_with_an_empty_kind_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Record(""));
        Assert.Matches(@"(?s)^An external event needs a kind that is a plain name.*; '' is not\.$", exception.Message);
    }

    [Fact]
    public void An_external_event_whose_kind_is_not_a_plain_name_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Record("pr assigned!"));
        Assert.Equal($"An external event needs a kind that is a plain name (letters, digits, hyphens, and underscores) or a module's name and a plain name joined by a slash (<module>/<name>), where a module's name is {ModuleNames.Rule}; 'pr assigned!' is not.", exception.Message);
    }

    [Fact]
    public void An_external_event_of_a_module_prefixed_kind_is_recorded_by_the_module_its_prefix_names()
    {
        ExternalEventRecorded recorded = Record("software/pr-opened", recordedBy: Principal.ForModule("software"));

        Assert.Equal("software/pr-opened", recorded.Kind);
        Assert.Equal("module:software", recorded.RecordedBy);
    }

    public static TheoryData<string, Role, string?> NotTheNamedModule => new()
    {
        { "brian", Role.Owner, null },
        { "teammate", Role.Member, null },
        { "module:other", Role.Module, "other" },
    };

    [Theory]
    [MemberData(nameof(NotTheNamedModule))]
    public void An_external_event_of_a_module_prefixed_kind_is_refused_to_every_principal_but_the_module_its_prefix_names(
        string id, Role role, string? module)
    {
        DomainException exception = Assert.Throws<DomainException>(
            () => Record("software/pr-opened", recordedBy: new Principal(id, role, Module: module)));

        Assert.Equal($"Only module 'software' records external events of kinds prefixed 'software/'; '{id}' cannot record 'software/pr-opened'.", exception.Message);
    }

    [Fact]
    public void An_external_event_of_a_plain_kind_is_recorded_by_a_member()
    {
        Assert.Equal("teammate", Record("pr-assigned", recordedBy: Member).RecordedBy);
    }

    [Theory]
    [InlineData("software/pr/opened")]
    [InlineData("/pr-opened")]
    [InlineData("software/")]
    [InlineData("soft ware/pr-opened")]
    public void An_external_event_whose_kind_is_neither_a_plain_name_nor_one_prefixed_plain_name_is_refused(string kind)
    {
        DomainException exception = Assert.Throws<DomainException>(() => Record(kind));
        Assert.EndsWith($"(<module>/<name>), where a module's name is {ModuleNames.Rule}; '{kind}' is not.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_external_event_of_the_parcel_entered_kind_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Record("parcel-entered"));
        Assert.Equal("The kind 'parcel-entered' is what a parcel's own creation and moves read as; an external event cannot use it.", exception.Message);
    }

    [Fact]
    public void An_external_event_of_the_flow_run_ended_kind_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Record("flow-run-ended", new Dictionary<string, string> { ["flow"] = "build", ["outcome"] = "pr-opened", ["type"] = "task" }));
        Assert.Equal("The kind 'flow-run-ended' is what a flow run's own end reads as; an external event cannot use it.", exception.Message);
    }

    [Fact]
    public void An_external_event_of_the_attention_kind_is_refused_whoever_records_it()
    {
        foreach (Principal recorder in new[] { Owner, Member, Principal.ForModule("software") })
        {
            DomainException exception = Assert.Throws<DomainException>(() => Record("attention", new Dictionary<string, string> { ["flow"] = "build" }, recorder));
            Assert.Equal("The kind 'attention' is what a flow run's pause reads as; an external event cannot use it.", exception.Message);
        }
    }

    [Fact]
    public void An_external_event_of_a_module_prefixed_attention_kind_is_recorded()
    {
        ExternalEventRecorded recorded = Record("software/attention", recordedBy: Principal.ForModule("software"));

        Assert.Equal("software/attention", recorded.Kind);
    }

    [Fact]
    public void An_external_event_with_a_field_name_that_is_not_plain_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Record("pr-assigned", new Dictionary<string, string> { ["assigned to"] = "brian" }));
        Assert.Equal("An external event's field names are plain names (letters, digits, hyphens, and underscores); 'assigned to' is not.", exception.Message);
    }

    [Fact]
    public void A_subscription_recorded_before_triggers_were_definitions_folds_its_trigger_what_it_creates_and_the_principal_that_added_it()
    {
        // No decider records a subscription any more, so the record is written as one already in the store reads.
        var recorded = new SubscriptionRecorded(
            Ids.New(), "alpha", "pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian" },
            "task", "title", "review-on-assignment", Member.Id, Member.Role, Now);
        Subscription subscription = Subscription.Create(recorded);

        Assert.Equal("alpha", subscription.Project);
        Assert.Equal("pr-assigned", subscription.Trigger.EventKind);
        Assert.Equivalent(new Dictionary<string, string> { ["assignee"] = "brian" }, subscription.Trigger.Filters, strict: true);
        Assert.Equal("task", subscription.ParcelType);
        Assert.Equal("title", subscription.TitleField);
        Assert.Equal("review-on-assignment", subscription.Flow);
        Assert.Equal(Member, subscription.Principal);
        Guid eventId = Ids.New();
        Assert.Equal($"subscription:{recorded.SubscriptionId}:event:{eventId}", subscription.OriginFor(eventId));
    }

    [Fact]
    public void No_decision_records_a_new_subscription()
    {
        Assert.DoesNotContain(
            typeof(TriggerDecider).GetMethods(),
            method => method.ReturnType == typeof(SubscriptionRecorded));
    }
}
