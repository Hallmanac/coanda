namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// The daemon is about to dispatch the system step <paramref name="SystemStep"/> of the flow run's
/// current step under <paramref name="DispatchId"/>, which it minted for this dispatch and records
/// before the call, so the step finds the id on the flow run while it runs and a restart finds a
/// dispatch to ask the status of. It counts nothing and leaves the visit's resume as it was, since
/// the dispatch carries that resume to the step.
/// </summary>
public sealed record SystemStepDispatched(
    Guid FlowRunId,
    string Step,
    string SystemStep,
    Guid DispatchId,
    DateTimeOffset DispatchedAt);
