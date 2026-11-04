using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Api;

public sealed record ParcelAddRequest(string Project, string Type, string Title, string? State);

public sealed record ParcelMoveRequest(string Project, Guid ParcelId, string To);

public sealed record ParcelCloseRequest(string Project, Guid ParcelId, string Resolution, string Reason);

public sealed record FlowStartRequest(string Project, Guid ParcelId, string Contour);

public sealed record ParcelView(
    Guid Id,
    string Project,
    string Type,
    string TypeHash,
    string State,
    string Title,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Resolution,
    string? CloseReason,
    Guid? LiveFlowId)
{
    public static ParcelView From(Parcel parcel) => new(
        parcel.Id, parcel.Project, parcel.TypeName, parcel.TypeHash, parcel.State, parcel.Title,
        parcel.CreatedBy, parcel.CreatedAt, parcel.UpdatedAt, parcel.Resolution, parcel.CloseReason, parcel.LiveFlowId);
}

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

public sealed record TypeView(string Name, string Hash, IReadOnlyList<string> States, IReadOnlyList<string> Resolutions);

public sealed record ContourView(string Name, int Version, string Hash);

public sealed record AgentView(string Name, int Version, ModelSpec Model, string Hash)
{
    public static AgentView From(Definition definition) => new(
        definition.Name, definition.VersionNumber, AgentDefinition.FromJson(definition.Content).Model, definition.ContentHash);
}

public sealed record DaemonStatusView(int Pid, int Port, DateTimeOffset StartedAt, string Version, string Home, string Database);

public sealed record ErrorView(string Error);
