using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Core.Triggers.Events;

namespace Coanda.Core.Triggers;

/// <summary>
/// The rules for recording an external event, as pure decisions: the event that records the intent, or
/// a <see cref="DomainException"/> quoting the rule. It may not use
/// <see cref="TriggerEvent.ParcelEnteredKind"/> or <see cref="TriggerEvent.FlowRunEndedKind"/>: those
/// are what the daemon's own record reads as, and a recorded event of either kind would pass for a
/// parcel entry or a flow run end. No new subscription is recorded: a trigger is a definition now, and
/// the subscriptions recorded before stay in force (<see cref="Subscription"/>).
/// An external event of a module-prefixed kind (<c>&lt;module&gt;/&lt;name&gt;</c>) is recorded only by
/// the Module principal of the module the prefix names; the owner is refused too, and repairs a
/// module's record through the module.
/// </summary>
public static class TriggerDecider
{
    /// <summary>
    /// The rule a recording with no external id, or a blank one, quotes: the id is what makes recording
    /// idempotent on the project, the kind, and that id.
    /// </summary>
    public const string ExternalIdRule = "An external event needs the id the outside world gives it, which recording it again matches on";

    /// <summary>
    /// Records an external event of <paramref name="kind"/> whose id in the outside world is
    /// <paramref name="externalId"/>, kept exactly as given, and which concerns <paramref name="parcelId"/>
    /// when the recorder names one. Whether an event with the same project, kind, and external id is
    /// already recorded is the daemon's to check, after these rules pass.
    /// </summary>
    public static ExternalEventRecorded RecordExternalEvent(
        string project,
        string kind,
        string? externalId,
        Guid? parcelId,
        IReadOnlyDictionary<string, string> fields,
        Principal recordedBy,
        DateTimeOffset now)
    {
        string trimmedProject = RequireProject(project);

        if (!Flow.IsPlainName(kind) && !ModuleNames.IsPrefixed(kind))
        {
            throw new DomainException(
                $"An external event needs a kind that is a plain name (letters, digits, hyphens, and underscores) or a module's name and a plain name joined by a slash (<module>/<name>), where a module's name is {ModuleNames.Rule}; '{kind}' is not.");
        }

        if (ModuleNames.PrefixOf(kind) is { } prefix
            && !(recordedBy.Role == Role.Module && string.Equals(recordedBy.Module, prefix, StringComparison.Ordinal)))
        {
            throw new DomainException(
                $"Only module '{prefix}' records external events of kinds prefixed '{prefix}/'; '{recordedBy.Id}' cannot record '{kind}'.");
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

        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new DomainException($"{ExternalIdRule}; none was given.");
        }

        if (externalId.Length > ExternalEventCaps.ExternalIdLength)
        {
            throw new DomainException(
                $"An external event's external id is at most {ExternalEventCaps.ExternalIdLength} characters; this one is {externalId.Length}.");
        }

        return new ExternalEventRecorded(
            Ids.New(),
            trimmedProject,
            kind,
            new Dictionary<string, string>(fields, StringComparer.Ordinal),
            recordedBy.Id,
            now,
            externalId,
            parcelId);
    }

    private static string RequireProject(string project) =>
        string.IsNullOrWhiteSpace(project)
            ? throw new DomainException("An external event belongs to a project; none was given.")
            : project.Trim();
}
