namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// A flow run began on a parcel and entered its flow's first step. The step's kind and the agent or
/// system step it names ride on the event, so replay counts the first spawn without the flow.
/// </summary>
public sealed record FlowRunStarted(
    Guid FlowRunId,
    Guid ParcelId,
    string FlowName,
    int FlowVersion,
    string FlowHash,
    string StartedBy,
    string FirstStep,
    StepKind FirstStepKind,
    string FirstStepPerformer,
    DateTimeOffset StartedAt,
    Guid? ParentFlowRunId = null,
    Guid? RootFlowRunId = null,
    int Depth = 0);
