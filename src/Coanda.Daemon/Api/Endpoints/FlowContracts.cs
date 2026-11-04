using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.Flows;

namespace Coanda.Daemon.Api.Endpoints;

public sealed record FlowStartRequest(string Project, Guid ParcelId, string Contour);

public sealed record FlowStopRequest(string Project, string Reason);

public sealed record FlowResumeRequest(string Project, string? Note);

/// <summary>
/// A flow as the loopback API carries it. <see cref="Status"/> is running, paused, ended, or
/// stopped; a running or paused flow is the live one its parcel names. A flow a follow started
/// carries its <see cref="ParentFlowId"/>, and every flow its <see cref="RootFlowId"/> and
/// <see cref="Depth"/>, zero for a root.
/// </summary>
public sealed record FlowView(
    Guid Id,
    string Project,
    Guid ParcelId,
    string ContourName,
    int ContourVersion,
    string ContourHash,
    string Status,
    string CurrentStep,
    int Spawns,
    string StartedBy,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<FlowStepView> Steps,
    string? PauseReason,
    string? Lever,
    string? Outcome,
    string? StopReason,
    Guid? ParentFlowId,
    Guid RootFlowId,
    int Depth,
    decimal Cost,
    Measurement? PauseUsage)
{
    public static FlowView From(string project, Flow flow) => new(
        flow.Id, project, flow.ParcelId, flow.ContourName, flow.ContourVersion, flow.ContourHash,
        StatusName(flow.Status), flow.CurrentStep, flow.Spawns, flow.StartedBy, flow.StartedAt, flow.UpdatedAt,
        [.. flow.Steps.Select(FlowStepView.From)], flow.PauseReason, flow.Lever, flow.Outcome, flow.StopReason,
        flow.ParentFlowId, flow.RootFlowId, flow.Depth, flow.Cost, flow.PauseUsage);

    public static string StatusName(FlowStatus status) => status switch
    {
        FlowStatus.Running => "running",
        FlowStatus.Paused => "paused",
        FlowStatus.Ended => "ended",
        FlowStatus.Stopped => "stopped",
        _ => status.ToString().ToLowerInvariant(),
    };
}

/// <summary>One step a flow entered, with its answer and measurement (a judgment) or its result (an action) once recorded.</summary>
public sealed record FlowStepView(
    string Step,
    string Kind,
    string Performer,
    DateTimeOffset StartedAt,
    string? AgentHash,
    ModelSpec? Model,
    JsonElement? Answer,
    Measurement? Measurement,
    JsonElement? Result,
    DateTimeOffset? RecordedAt)
{
    public static FlowStepView From(FlowStepEntry entry) => new(
        entry.Step,
        entry.Kind == StepKind.Judgment ? "judgment" : "action",
        entry.Performer, entry.StartedAt, entry.AgentHash, entry.Model, entry.Answer, entry.Measurement, entry.Result, entry.RecordedAt);
}
