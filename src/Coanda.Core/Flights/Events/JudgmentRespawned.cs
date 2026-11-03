namespace Coanda.Core.Flights.Events;

/// <summary>
/// A resumed judgment step whose visit parked without an answer is performed again on the same
/// visit, so <paramref name="Agent"/> is spawned once more: one more spawn for the flight, and no
/// visit, since the step was not entered again.
/// </summary>
public sealed record JudgmentRespawned(
    Guid FlightId,
    string Step,
    string Agent,
    DateTimeOffset RespawnedAt);
