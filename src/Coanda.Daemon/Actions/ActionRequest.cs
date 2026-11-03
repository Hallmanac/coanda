using Coanda.Core.Flows;
using Coanda.Core.Registry;

namespace Coanda.Daemon.Actions;

/// <summary>
/// One action step to perform: who performs it (the flight's principal), the flight and its parcel,
/// the flow and the step. <paramref name="Resumed"/> is true when the flight was resumed while this
/// visit was its current step, which is the event a wait step waits for.
/// </summary>
public sealed record ActionRequest(
    Principal Principal,
    string Project,
    Guid FlightId,
    Guid ParcelId,
    FlowDefinition Flow,
    FlowStep Step,
    bool Resumed);
