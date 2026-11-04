namespace Coanda.Core.Flows.Events;

/// <summary>
/// A flow began on a parcel and entered its contour's first step. The step's kind and the agent or
/// action it names ride on the event, so replay counts the first spawn without the contour.
/// </summary>
public sealed record FlowStarted(
    Guid FlowId,
    Guid ParcelId,
    string ContourName,
    int ContourVersion,
    string ContourHash,
    string StartedBy,
    string FirstStep,
    StepKind FirstStepKind,
    string FirstStepPerformer,
    DateTimeOffset StartedAt,
    Guid? ParentFlowId = null,
    Guid? RootFlowId = null,
    int Depth = 0);
