using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;

namespace Coanda.Daemon.Api.Endpoints;

public sealed record FlowRunStartRequest(string Project, Guid ParcelId, string Contour);

public sealed record FlowRunPauseRequest(string Project, string Reason);

public sealed record FlowRunStopRequest(string Project, string Reason);

public sealed record FlowRunResumeRequest(string Project, string? Note);

/// <summary>
/// A flow run as the loopback API carries it. <see cref="Status"/> is running, paused, ended, or
/// stopped; a running or paused flow run is the live one its parcel names. A flow run a follow started
/// carries its <see cref="ParentFlowRunId"/>, and every flow run its <see cref="RootFlowRunId"/> and
/// <see cref="Depth"/>, zero for a root.
/// </summary>
public sealed record FlowRunView(
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
    IReadOnlyList<FlowRunStepView> Steps,
    string? PauseReason,
    string? Lever,
    string? Outcome,
    string? StopReason,
    Guid? ParentFlowRunId,
    Guid RootFlowRunId,
    int Depth,
    decimal Cost,
    Measurement? PauseUsage)
{
    public static FlowRunView From(string project, FlowRun flowRun) => new(
        flowRun.Id, project, flowRun.ParcelId, flowRun.ContourName, flowRun.ContourVersion, flowRun.ContourHash,
        StatusName(flowRun.Status), flowRun.CurrentStep, flowRun.Spawns, flowRun.StartedBy, flowRun.StartedAt, flowRun.UpdatedAt,
        [.. flowRun.Steps.Select(FlowRunStepView.From)], flowRun.PauseReason, flowRun.Lever, flowRun.Outcome, flowRun.StopReason,
        flowRun.ParentFlowRunId, flowRun.RootFlowRunId, flowRun.Depth, flowRun.Cost, flowRun.PauseUsage);

    public static string StatusName(FlowRunStatus status) => status switch
    {
        FlowRunStatus.Running => "running",
        FlowRunStatus.Paused => "paused",
        FlowRunStatus.Ended => "ended",
        FlowRunStatus.Stopped => "stopped",
        _ => status.ToString().ToLowerInvariant(),
    };
}

/// <summary>One step a flow run entered, with its answer and measurement (a judgment) or its result (an action) once recorded.</summary>
public sealed record FlowRunStepView(
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
    public static FlowRunStepView From(FlowRunStepEntry entry) => new(
        entry.Step,
        entry.Kind == StepKind.Judgment ? "judgment" : "action",
        entry.Performer, entry.StartedAt, entry.AgentHash, entry.Model, entry.Answer, entry.Measurement, entry.Result, entry.RecordedAt);
}
