namespace Coanda.Core.FlowRuns;

/// <summary>
/// The dispatch a write names as the one it is made under: the flow run that recorded it and its
/// dispatch id.
/// </summary>
public sealed record NamedDispatch(Guid FlowRunId, Guid DispatchId);
