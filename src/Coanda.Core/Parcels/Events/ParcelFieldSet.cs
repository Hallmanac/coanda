namespace Coanda.Core.Parcels.Events;

/// <summary>A value was recorded in one of the parcel's fields, replacing whatever the field held before.</summary>
public sealed record ParcelFieldSet(
    Guid ParcelId,
    string Field,
    FieldValue Value,
    string SetBy,
    DateTimeOffset SetAt);
