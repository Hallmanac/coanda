namespace Coanda.Sdk.Contract;

/// <summary>
/// An external event a module records through the core's API (<c>recorded-event.schema.json</c>):
/// its module-prefixed kind, the id the outside world gives it, which makes recording it again a
/// no-op, its string fields, which are untrusted text, and the parcel it is about when the module
/// looked that up.
/// </summary>
public sealed record RecordedEvent(
    string Project,
    string Kind,
    string ExternalId,
    IReadOnlyDictionary<string, string> Fields,
    Guid? ParcelId = null);
