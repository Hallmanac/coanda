namespace Coanda.Core.Flights.Events;

/// <summary>
/// A flight stopped to wait for a person; <paramref name="Lever"/> is what that person acts on to
/// resume it. A judgment that paused without an answer records the <paramref name="Usage"/> its
/// attempts reported, since what they cost counts against the budget as an answer's cost does.
/// </summary>
public sealed record FlightPaused(
    Guid FlightId,
    string Reason,
    string Lever,
    string PausedBy,
    DateTimeOffset PausedAt,
    Measurement? Usage = null);
