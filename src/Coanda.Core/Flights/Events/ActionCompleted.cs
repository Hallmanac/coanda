using System.Text.Json;

namespace Coanda.Core.Flights.Events;

/// <summary>The action of the flight's current step finished and returned a result document.</summary>
public sealed record ActionCompleted(
    Guid FlightId,
    string Step,
    string Action,
    JsonElement Result,
    DateTimeOffset CompletedAt);
