namespace Coanda.Core.Flights.Events;

/// <summary>A flight stopped to wait for a person; <paramref name="Lever"/> is what that person acts on to resume it.</summary>
public sealed record FlightParked(
    Guid FlightId,
    string Reason,
    string Lever,
    string ParkedBy,
    DateTimeOffset ParkedAt);
