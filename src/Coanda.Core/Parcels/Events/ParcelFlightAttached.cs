namespace Coanda.Core.Parcels.Events;

/// <summary>
/// A flight started on the parcel and is now its live flight. Written to the parcel stream in the
/// same unit of work as the flight's start, so the parcel stream's optimistic concurrency is what
/// keeps two racing starts from both succeeding.
/// </summary>
public sealed record ParcelFlightAttached(
    Guid ParcelId,
    Guid FlightId,
    DateTimeOffset AttachedAt);
