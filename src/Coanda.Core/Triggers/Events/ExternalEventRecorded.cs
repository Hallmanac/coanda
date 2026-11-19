namespace Coanda.Core.Triggers.Events;

/// <summary>
/// Something outside the node happened in a project: a kind and named string fields, as an adapter
/// reported it. <paramref name="ExternalId"/> is the id the outside world gives the event, which makes
/// recording it idempotent on the project, the kind, and that id; <paramref name="ParcelId"/> is the
/// parcel the recorder said the event concerns, stored as given. An event recorded before either was
/// carried replays with both null, and a later recording matches it by neither.
/// </summary>
public sealed record ExternalEventRecorded(
    Guid EventId,
    string Project,
    string Kind,
    IReadOnlyDictionary<string, string> Fields,
    string RecordedBy,
    DateTimeOffset RecordedAt,
    string? ExternalId = null,
    Guid? ParcelId = null);
