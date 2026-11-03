namespace Coanda.Core.Flights.Events;

public sealed record FlightResumed(
    Guid FlightId,
    string ResumedBy,
    string Note,
    DateTimeOffset ResumedAt);
