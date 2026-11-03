namespace Coanda.Core.Parcels.Events;

/// <summary>The parcel's live flight ended or was stopped, so the parcel has none and a new flight may start.</summary>
public sealed record ParcelFlightReleased(
    Guid ParcelId,
    Guid FlightId,
    DateTimeOffset ReleasedAt);
