namespace Coanda.Core.Flights.Events;

/// <summary>A flight entered a step; <paramref name="Performer"/> is the agent or action the step names.</summary>
public sealed record StepStarted(
    Guid FlightId,
    string Step,
    StepKind Kind,
    string Performer,
    DateTimeOffset StartedAt);
