namespace Coanda.Core.Parcels.Events;

/// <summary>
/// A parcel came into being. <see cref="Origin"/> names what created it when that was not a person's
/// add (a subscription firing on an event, say), so the creation is never made twice for one cause;
/// a parcel created before origins existed reads it as null.
/// </summary>
public sealed record ParcelCreated(
    Guid ParcelId,
    string Project,
    string TypeName,
    string TypeHash,
    string State,
    string Title,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    string? Origin = null);
