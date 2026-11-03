namespace Coanda.Core.Flights.Events;

/// <summary>
/// A flight began on a parcel and entered its flow's first step. The step's kind and the agent or
/// action it names ride on the event, so replay counts the first spawn without the flow.
/// </summary>
public sealed record FlightStarted(
    Guid FlightId,
    Guid ParcelId,
    string FlowName,
    int FlowVersion,
    string FlowHash,
    string StartedBy,
    string FirstStep,
    StepKind FirstStepKind,
    string FirstStepPerformer,
    DateTimeOffset StartedAt);
