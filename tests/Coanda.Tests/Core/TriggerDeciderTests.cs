using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;

namespace Coanda.Tests.Core;

public sealed class TriggerDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Principal Member = new("teammate", Role.Member);
    private static readonly Dictionary<string, string> NoFields = [];

    private static ExternalEventRecorded Record(string kind, Dictionary<string, string>? fields = null) =>
        TriggerDecider.RecordExternalEvent("alpha", kind, fields ?? NoFields, "brian", Now);

    private static SubscriptionRecorded Subscribe(
        string kind = "pr-assigned",
        Dictionary<string, string>? filters = null,
        string parcelType = "task",
        string titleField = "title",
        string contour = "review-on-assignment") =>
        TriggerDecider.RecordSubscription(
            "alpha", new Trigger(kind, filters ?? new Dictionary<string, string> { ["assignee"] = "brian" }), parcelType, titleField, contour, Member, Now);

    [Fact]
    public void An_external_event_records_its_kind_and_fields()
    {
        ExternalEventRecorded recorded = Record("pr-assigned", new Dictionary<string, string> { ["assignee"] = "brian", ["pr_number"] = "12" });

        recorded.Project.Should().Be("alpha");
        recorded.Kind.Should().Be("pr-assigned");
        recorded.Fields.Should().BeEquivalentTo(new Dictionary<string, string> { ["assignee"] = "brian", ["pr_number"] = "12" });
        recorded.RecordedBy.Should().Be("brian");
    }

    [Fact]
    public void An_external_event_with_an_empty_kind_is_refused() =>
        FluentActions.Invoking(() => Record(""))
            .Should().Throw<DomainException>().WithMessage("An external event needs a kind that is a plain name*; '' is not.");

    [Fact]
    public void An_external_event_whose_kind_is_not_a_plain_name_is_refused() =>
        FluentActions.Invoking(() => Record("pr assigned!"))
            .Should().Throw<DomainException>().WithMessage("An external event needs a kind that is a plain name (letters, digits, hyphens, and underscores); 'pr assigned!' is not.");

    [Fact]
    public void An_external_event_of_the_parcel_entered_kind_is_refused() =>
        FluentActions.Invoking(() => Record("parcel-entered"))
            .Should().Throw<DomainException>().WithMessage("The kind 'parcel-entered' is what a parcel's own creation and moves read as; an external event cannot use it.");

    [Fact]
    public void An_external_event_of_the_flow_ended_kind_is_refused() =>
        FluentActions.Invoking(() => Record("flow-ended", new Dictionary<string, string> { ["contour"] = "build", ["outcome"] = "pr-opened", ["type"] = "task" }))
            .Should().Throw<DomainException>().WithMessage("The kind 'flow-ended' is what a flow's own end reads as; an external event cannot use it.");

    [Fact]
    public void An_external_event_with_a_field_name_that_is_not_plain_is_refused() =>
        FluentActions.Invoking(() => Record("pr-assigned", new Dictionary<string, string> { ["assigned to"] = "brian" }))
            .Should().Throw<DomainException>().WithMessage("An external event's field names are plain names (letters, digits, hyphens, and underscores); 'assigned to' is not.");

    [Fact]
    public void A_subscription_records_its_trigger_what_it_creates_and_the_principal_that_added_it()
    {
        Subscription subscription = Subscription.Create(Subscribe());

        subscription.Project.Should().Be("alpha");
        subscription.Trigger.EventKind.Should().Be("pr-assigned");
        subscription.Trigger.Filters.Should().BeEquivalentTo(new Dictionary<string, string> { ["assignee"] = "brian" });
        subscription.ParcelType.Should().Be("task");
        subscription.TitleField.Should().Be("title");
        subscription.Contour.Should().Be("review-on-assignment");
        subscription.Principal.Should().Be(Member);
    }

    [Fact]
    public void A_subscription_with_an_empty_event_kind_is_refused() =>
        FluentActions.Invoking(() => Subscribe(kind: " "))
            .Should().Throw<DomainException>().WithMessage("A subscription's trigger needs an event kind.");

    [Fact]
    public void A_subscription_on_the_parcel_entered_kind_is_refused() =>
        FluentActions.Invoking(() => Subscribe(kind: "parcel-entered"))
            .Should().Throw<DomainException>().WithMessage("A subscription sees external events only; it cannot trigger on 'parcel-entered', which a parcel's own creation and moves read as.");

    [Fact]
    public void A_subscription_on_the_flow_ended_kind_is_refused() =>
        FluentActions.Invoking(() => Subscribe(kind: "flow-ended", filters: new Dictionary<string, string> { ["outcome"] = "pr-opened" }))
            .Should().Throw<DomainException>().WithMessage("A subscription sees external events only; it cannot trigger on 'flow-ended', which a flow's own end reads as.");

    [Fact]
    public void A_subscription_with_a_filter_name_that_is_not_plain_is_refused() =>
        FluentActions.Invoking(() => Subscribe(filters: new Dictionary<string, string> { ["assignee.login"] = "brian" }))
            .Should().Throw<DomainException>().WithMessage("A subscription's filters name event fields, which are plain names (letters, digits, hyphens, and underscores); 'assignee.login' is not.");

    [Fact]
    public void A_subscription_with_no_parcel_type_is_refused() =>
        FluentActions.Invoking(() => Subscribe(parcelType: ""))
            .Should().Throw<DomainException>().WithMessage("A subscription names the parcel type it creates; none was given.");

    [Fact]
    public void A_subscription_with_no_title_field_is_refused() =>
        FluentActions.Invoking(() => Subscribe(titleField: ""))
            .Should().Throw<DomainException>().WithMessage("A subscription names the event field whose value titles the parcel it creates; none was given.");

    [Fact]
    public void A_subscription_with_no_contour_is_refused() =>
        FluentActions.Invoking(() => Subscribe(contour: ""))
            .Should().Throw<DomainException>().WithMessage("A subscription names the contour it starts on the parcel it creates; none was given.");
}
