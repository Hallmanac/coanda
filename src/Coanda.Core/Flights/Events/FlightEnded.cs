namespace Coanda.Core.Flights.Events;

/// <summary>A flight reached the end of its flow and carries the outcome the flow declares.</summary>
public sealed record FlightEnded(
    Guid FlightId,
    string Outcome,
    string EndedBy,
    DateTimeOffset EndedAt);
