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
        TriggerDecider.RecordExternalEvent("alpha", kind, fields ?? NoFields, recordedBy ?? Owner, Now);

    private static SubscriptionRecorded Subscribe(
        string kind = "pr-assigned",
        Dictionary<string, string>? filters = null,
        string parcelType = "task",
        string titleField = "title",
        string flow = "review-on-assignment") =>
        TriggerDecider.RecordSubscription(
            "alpha", new Trigger(kind, filters ?? new Dictionary<string, string> { ["assignee"] = "brian" }), parcelType, titleField, flow, Member, Now);

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
    public void An_external_event_with_a_field_name_that_is_not_plain_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Record("pr-assigned", new Dictionary<string, string> { ["assigned to"] = "brian" }));
        Assert.Equal("An external event's field names are plain names (letters, digits, hyphens, and underscores); 'assigned to' is not.", exception.Message);
    }

    [Fact]
    public void A_subscription_records_its_trigger_what_it_creates_and_the_principal_that_added_it()
    {
        Subscription subscription = Subscription.Create(Subscribe());

        Assert.Equal("alpha", subscription.Project);
        Assert.Equal("pr-assigned", subscription.Trigger.EventKind);
        Assert.Equivalent(new Dictionary<string, string> { ["assignee"] = "brian" }, subscription.Trigger.Filters, strict: true);
        Assert.Equal("task", subscription.ParcelType);
        Assert.Equal("title", subscription.TitleField);
        Assert.Equal("review-on-assignment", subscription.Flow);
        Assert.Equal(Member, subscription.Principal);
    }

    [Fact]
    public void A_subscription_with_an_empty_event_kind_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Subscribe(kind: " "));
        Assert.Equal("A subscription's trigger needs an event kind.", exception.Message);
    }

    [Fact]
    public void A_subscription_on_the_parcel_entered_kind_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Subscribe(kind: "parcel-entered"));
        Assert.Equal("A subscription sees external events only; it cannot trigger on 'parcel-entered', which a parcel's own creation and moves read as.", exception.Message);
    }

    [Fact]
    public void A_subscription_on_the_flow_run_ended_kind_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Subscribe(kind: "flow-run-ended", filters: new Dictionary<string, string> { ["outcome"] = "pr-opened" }));
        Assert.Equal("A subscription sees external events only; it cannot trigger on 'flow-run-ended', which a flow run's own end reads as.", exception.Message);
    }

    [Fact]
    public void A_subscription_with_a_filter_name_that_is_not_plain_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Subscribe(filters: new Dictionary<string, string> { ["assignee.login"] = "brian" }));
        Assert.Equal("A subscription's filters name event fields, which are plain names (letters, digits, hyphens, and underscores); 'assignee.login' is not.", exception.Message);
    }

    [Fact]
    public void A_subscription_with_no_parcel_type_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Subscribe(parcelType: ""));
        Assert.Equal("A subscription names the parcel type it creates; none was given.", exception.Message);
    }

    [Fact]
    public void A_subscription_with_no_title_field_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Subscribe(titleField: ""));
        Assert.Equal("A subscription names the event field whose value titles the parcel it creates; none was given.", exception.Message);
    }

    [Fact]
    public void A_subscription_with_no_flow_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Subscribe(flow: ""));
        Assert.Equal("A subscription names the flow it starts on the parcel it creates; none was given.", exception.Message);
    }
}
