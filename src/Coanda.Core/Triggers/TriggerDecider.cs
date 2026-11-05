using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Core.Triggers.Events;

namespace Coanda.Core.Triggers;

/// <summary>
/// The rules for recording an external event and a subscription, as pure decisions: the event that
/// records the intent, or a <see cref="DomainException"/> quoting the rule. Neither may use
/// <see cref="TriggerEvent.ParcelEnteredKind"/> or <see cref="TriggerEvent.FlowRunEndedKind"/>: those
/// are what the daemon's own record reads as, a subscription that created parcels from them could
/// feed itself, and a recorded event of either kind would pass for a parcel entry or a flow run end.
/// </summary>
public static class TriggerDecider
{
    public static ExternalEventRecorded RecordExternalEvent(
        string project, string kind, IReadOnlyDictionary<string, string> fields, string recordedBy, DateTimeOffset now)
    {
        string trimmedProject = RequireProject(project);

        if (!Flow.IsPlainName(kind) && !ModuleNames.IsPrefixed(kind))
        {
            throw new DomainException(
                $"An external event needs a kind that is a plain name (letters, digits, hyphens, and underscores) or a module's name and a plain name joined by a slash (<module>/<name>); '{kind}' is not.");
        }

        if (string.Equals(kind, TriggerEvent.ParcelEnteredKind, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"The kind '{TriggerEvent.ParcelEnteredKind}' is what a parcel's own creation and moves read as; an external event cannot use it.");
        }

        if (string.Equals(kind, TriggerEvent.FlowRunEndedKind, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"The kind '{TriggerEvent.FlowRunEndedKind}' is what a flow run's own end reads as; an external event cannot use it.");
        }

        string? notPlain = fields.Keys.FirstOrDefault(name => !Flow.IsPlainName(name));
        if (notPlain is not null)
        {
            throw new DomainException(
                $"An external event's field names are plain names (letters, digits, hyphens, and underscores); '{notPlain}' is not.");
        }

        return new ExternalEventRecorded(
            Ids.New(), trimmedProject, kind, new Dictionary<string, string>(fields, StringComparer.Ordinal), recordedBy, now);
    }

    public static SubscriptionRecorded RecordSubscription(
        string project,
        Trigger trigger,
        string parcelType,
        string titleField,
        string flow,
        Principal recordedBy,
        DateTimeOffset now)
    {
        string trimmedProject = RequireProject(project);

        if (string.IsNullOrWhiteSpace(trigger.EventKind))
        {
            throw new DomainException("A subscription's trigger needs an event kind.");
        }

        if (string.Equals(trigger.EventKind, TriggerEvent.ParcelEnteredKind, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"A subscription sees external events only; it cannot trigger on '{TriggerEvent.ParcelEnteredKind}', which a parcel's own creation and moves read as.");
        }

        if (string.Equals(trigger.EventKind, TriggerEvent.FlowRunEndedKind, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"A subscription sees external events only; it cannot trigger on '{TriggerEvent.FlowRunEndedKind}', which a flow run's own end reads as.");
        }

        string? notPlain = trigger.Filters.Keys.FirstOrDefault(name => !Flow.IsPlainName(name));
        if (notPlain is not null)
        {
            throw new DomainException(
                $"A subscription's filters name event fields, which are plain names (letters, digits, hyphens, and underscores); '{notPlain}' is not.");
        }

        if (string.IsNullOrWhiteSpace(parcelType))
        {
            throw new DomainException("A subscription names the parcel type it creates; none was given.");
        }

        if (string.IsNullOrWhiteSpace(titleField))
        {
            throw new DomainException("A subscription names the event field whose value titles the parcel it creates; none was given.");
        }

        if (string.IsNullOrWhiteSpace(flow))
        {
            throw new DomainException("A subscription names the flow it starts on the parcel it creates; none was given.");
        }

        return new SubscriptionRecorded(
            Ids.New(),
            trimmedProject,
            trigger.EventKind,
            new Dictionary<string, string>(trigger.Filters, StringComparer.Ordinal),
            parcelType.Trim(),
            titleField.Trim(),
            flow.Trim(),
            recordedBy.Id,
            recordedBy.Role,
            now);
    }

    private static string RequireProject(string project) =>
        string.IsNullOrWhiteSpace(project)
            ? throw new DomainException("An external event or a subscription belongs to a project; none was given.")
            : project.Trim();
}
