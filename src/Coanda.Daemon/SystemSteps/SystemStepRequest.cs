using Coanda.Core.Flows;
using Coanda.Core.Registry;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// One system step to perform: who performs it (the flow run's principal), the flow run and its parcel,
/// the flow and the step. <paramref name="Resumed"/> is true when the flow run was resumed while this
/// visit was its current step, which is the event a wait step waits for.
/// </summary>
public sealed record SystemStepRequest(
    Principal Principal,
    string Project,
    Guid FlowRunId,
    Guid ParcelId,
    Flow Flow,
    FlowStep Step,
    bool Resumed);
