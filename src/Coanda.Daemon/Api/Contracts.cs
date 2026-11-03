using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flights;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Api;

public sealed record ParcelAddRequest(string Project, string Type, string Title, string? State);

public sealed record ParcelMoveRequest(string Project, Guid ParcelId, string To);

public sealed record ParcelCloseRequest(string Project, Guid ParcelId, string Resolution, string Reason);

public sealed record FlightStartRequest(string Project, Guid ParcelId, string Flow);

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
    Guid? LiveFlightId)
{
    public static ParcelView From(Parcel parcel) => new(
        parcel.Id, parcel.Project, parcel.TypeName, parcel.TypeHash, parcel.State, parcel.Title,
        parcel.CreatedBy, parcel.CreatedAt, parcel.UpdatedAt, parcel.Resolution, parcel.CloseReason, parcel.LiveFlightId);
}

/// <summary>
/// A flight as the loopback API carries it. <see cref="Status"/> is running, parked, ended, or
/// stopped; a running or parked flight is the live one its parcel names.
/// </summary>
public sealed record FlightView(
    Guid Id,
    string Project,
    Guid ParcelId,
    string Flow,
    int FlowVersion,
    string FlowHash,
    string Status,
    string CurrentStep,
    int Spawns,
    string StartedBy,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<FlightStepView> Steps,
    string? ParkReason,
    string? Lever,
    string? Outcome,
    string? StopReason)
{
    public static FlightView From(string project, Flight flight) => new(
        flight.Id, project, flight.ParcelId, flight.FlowName, flight.FlowVersion, flight.FlowHash,
        StatusName(flight.Status), flight.CurrentStep, flight.Spawns, flight.StartedBy, flight.StartedAt, flight.UpdatedAt,
        [.. flight.Steps.Select(FlightStepView.From)], flight.ParkReason, flight.Lever, flight.Outcome, flight.StopReason);

    public static string StatusName(FlightStatus status) => status switch
    {
        FlightStatus.Running => "running",
        FlightStatus.Parked => "parked",
        FlightStatus.Ended => "ended",
        FlightStatus.Stopped => "stopped",
        _ => status.ToString().ToLowerInvariant(),
    };
}

/// <summary>One step a flight entered, with its answer and measurement (a judgment) or its result (an action) once recorded.</summary>
public sealed record FlightStepView(
    string Step,
    string Kind,
    string Performer,
    DateTimeOffset StartedAt,
    string? AgentHash,
    ModelTriple? Model,
    JsonElement? Answer,
    Measurement? Measurement,
    JsonElement? Result,
    DateTimeOffset? RecordedAt)
{
    public static FlightStepView From(FlightStepEntry entry) => new(
        entry.Step,
        entry.Kind == StepKind.Judgment ? "judgment" : "action",
        entry.Performer, entry.StartedAt, entry.AgentHash, entry.Model, entry.Answer, entry.Measurement, entry.Result, entry.RecordedAt);
}

public sealed record TypeView(string Name, string Hash, IReadOnlyList<string> States, IReadOnlyList<string> Resolutions);

public sealed record FlowView(string Name, int Version, string Hash);

public sealed record AgentView(string Name, int Version, ModelTriple Model, string Hash)
{
    public static AgentView From(Definition definition) => new(
        definition.Name, definition.VersionNumber, AgentDefinition.FromJson(definition.Content).Model, definition.ContentHash);
}

public sealed record DaemonStatusView(int Pid, int Port, DateTimeOffset StartedAt, string Version, string Home, string Database);

public sealed record ErrorView(string Error);
