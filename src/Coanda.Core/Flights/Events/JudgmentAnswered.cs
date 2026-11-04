using System.Text.Json;
using Coanda.Core.Agents;

namespace Coanda.Core.Flights.Events;

/// <summary>An agent answered the flight's current judgment step, with what the answer cost.</summary>
public sealed record JudgmentAnswered(
    Guid FlightId,
    string Step,
    string Agent,
    string AgentHash,
    ModelSpec Model,
    JsonElement Answer,
    Measurement Measurement,
    DateTimeOffset AnsweredAt);
