namespace Coanda.Core.Flights.Events;

/// <summary>Someone stopped a live flight before its end, so its parcel is free for another.</summary>
public sealed record FlightStopped(
    Guid FlightId,
    string StoppedBy,
    string Reason,
    DateTimeOffset StoppedAt);
