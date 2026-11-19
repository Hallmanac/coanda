using Coanda.Core.Triggers;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>
/// An external event as the API shows it: who recorded it and when, the id the outside world gives it,
/// which an event recorded before external ids were carried has none of, and the parcel its recorder
/// said it concerns, when it named one.
/// </summary>
public sealed record ExternalEventView(
    Guid Id,
    string Project,
    string Kind,
    string? ExternalId,
    Guid? ParcelId,
    IReadOnlyDictionary<string, string> Fields,
    string RecordedBy,
    DateTimeOffset RecordedAt)
{
    public static ExternalEventView From(ExternalEvent external) => new(
        external.Id, external.Project, external.Kind, external.ExternalId, external.ParcelId, external.Fields, external.RecordedBy, external.RecordedAt);
}
