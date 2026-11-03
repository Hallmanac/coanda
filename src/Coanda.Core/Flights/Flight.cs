using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Coanda.Core.Flights.Events;
using Coanda.Core.Flows;

namespace Coanda.Core.Flights;

/// <summary>
/// One execution of a flow on a parcel. State changes only through <c>Apply</c>; the rules live in
/// <see cref="FlightDecider"/>. Replaying the events alone, without the flow, yields the same
/// <see cref="Position"/> the interpreter's start and apply produce, because every step entered
/// carries its kind. The store snapshots this class inline, so it is also the document the list
/// and show queries read.
/// </summary>
public sealed class Flight
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public Guid ParcelId { get; private set; }
    [JsonInclude]
    public string FlowName { get; private set; } = string.Empty;
    /// <summary>Not called <c>Version</c>: the store gives an aggregate property of that name the stream's event count.</summary>
    [JsonInclude]
    public int FlowVersion { get; private set; }
    [JsonInclude]
    public string FlowHash { get; private set; } = string.Empty;
    [JsonInclude]
    public string StartedBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset StartedAt { get; private set; }
    [JsonInclude]
    public DateTimeOffset UpdatedAt { get; private set; }
    [JsonInclude]
    public FlightStatus Status { get; private set; }
    [JsonInclude]
    public string CurrentStep { get; private set; } = string.Empty;
    [JsonInclude]
    public Dictionary<string, int> Visits { get; private set; } = new(StringComparer.Ordinal);
    [JsonInclude]
    public int Spawns { get; private set; }
    [JsonInclude]
    public List<FlightStepEntry> Steps { get; private set; } = [];
    [JsonInclude]
    public string? ParkReason { get; private set; }
    [JsonInclude]
    public string? Lever { get; private set; }
    [JsonInclude]
    public string? Outcome { get; private set; }
    [JsonInclude]
    public string? StopReason { get; private set; }

    /// <summary>The flight whose end started this one, or null when nothing did.</summary>
    [JsonInclude]
    public Guid? ParentFlightId { get; private set; }

    /// <summary>The first flight of this one's chain; a flight no follow started is its own root.</summary>
    [JsonInclude]
    public Guid RootFlightId { get; private set; }

    /// <summary>How many follows lie between this flight and its root: zero for the root.</summary>
    [JsonInclude]
    public int Depth { get; private set; }

    /// <summary>
    /// What the flight has recorded spending, in US dollars: the cost of every answered judgment and
    /// the usage every parked judgment reported.
    /// </summary>
    [JsonInclude]
    public decimal Cost { get; private set; }

    /// <summary>The usage the judgment that parked the flight reported, or null when the park was not a judgment's.</summary>
    [JsonInclude]
    public Measurement? ParkUsage { get; private set; }

    /// <summary>Running or parked: the flight still holds its parcel.</summary>
    public bool IsLive => Status is FlightStatus.Running or FlightStatus.Parked;

    /// <summary>The step entered last, which is the one an answer or a result is recorded against.</summary>
    [JsonIgnore]
    public FlightStepEntry? Current => Steps.Count == 0 ? null : Steps[^1];

    /// <summary>Where the flight stands, as the interpreter reads it.</summary>
    [JsonIgnore]
    public FlightPosition Position => new(CurrentStep, Visits.ToImmutableDictionary(StringComparer.Ordinal), Spawns);

    public static Flight Create(FlightStarted started)
    {
        var flight = new Flight();
        flight.Apply(started);
        return flight;
    }

    public void Apply(FlightStarted started)
    {
        Id = started.FlightId;
        ParcelId = started.ParcelId;
        FlowName = started.FlowName;
        FlowVersion = started.FlowVersion;
        FlowHash = started.FlowHash;
        StartedBy = started.StartedBy;
        StartedAt = started.StartedAt;
        ParentFlightId = started.ParentFlightId;
        RootFlightId = started.RootFlightId ?? started.FlightId;
        Depth = started.Depth;
        Status = FlightStatus.Running;
        Enter(started.FirstStep, started.FirstStepKind, started.FirstStepPerformer, started.StartedAt);
    }

    public void Apply(StepStarted started) =>
        Enter(started.Step, started.Kind, started.Performer, started.StartedAt);

    public void Apply(JudgmentAnswered answered)
    {
        Record(entry => entry with
        {
            AgentHash = answered.AgentHash,
            Model = answered.Model,
            Answer = answered.Answer,
            Measurement = answered.Measurement,
            RecordedAt = answered.AnsweredAt,
            ResumedAt = null,
        });
        Cost += answered.Measurement.CostUsd;
        UpdatedAt = answered.AnsweredAt;
    }

    public void Apply(ActionCompleted completed)
    {
        Record(entry => entry with { Result = completed.Result, RecordedAt = completed.CompletedAt, ResumedAt = null });
        UpdatedAt = completed.CompletedAt;
    }

    public void Apply(FlightParked parked)
    {
        Status = FlightStatus.Parked;
        ParkReason = parked.Reason;
        Lever = parked.Lever;
        ParkUsage = parked.Usage;
        Cost += parked.Usage?.CostUsd ?? 0m;
        UpdatedAt = parked.ParkedAt;
    }

    public void Apply(FlightResumed resumed)
    {
        Status = FlightStatus.Running;
        ParkReason = null;
        Lever = null;
        ParkUsage = null;
        Record(entry => entry with { ResumedAt = resumed.ResumedAt });
        UpdatedAt = resumed.ResumedAt;
    }

    public void Apply(FlightEnded ended)
    {
        Status = FlightStatus.Ended;
        Outcome = ended.Outcome;
        UpdatedAt = ended.EndedAt;
    }

    public void Apply(FlightStopped stopped)
    {
        Status = FlightStatus.Stopped;
        StopReason = stopped.Reason;
        UpdatedAt = stopped.StoppedAt;
    }

    /// <summary>One more visit to the step entered, and one more spawn when it is a judgment, as the interpreter counts them.</summary>
    private void Enter(string step, StepKind kind, string performer, DateTimeOffset at)
    {
        CurrentStep = step;
        Visits[step] = Visits.GetValueOrDefault(step) + 1;
        Spawns += kind == StepKind.Judgment ? 1 : 0;
        Steps.Add(new FlightStepEntry { Step = step, Kind = kind, Performer = performer, StartedAt = at });
        UpdatedAt = at;
    }

    private void Record(Func<FlightStepEntry, FlightStepEntry> change)
    {
        if (Steps.Count > 0)
        {
            Steps[^1] = change(Steps[^1]);
        }
    }
}
