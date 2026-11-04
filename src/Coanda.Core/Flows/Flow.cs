using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Coanda.Core.Flows.Events;
using Coanda.Core.Contours;

namespace Coanda.Core.Flows;

/// <summary>
/// One execution of a contour on a parcel. State changes only through <c>Apply</c>; the rules live in
/// <see cref="FlowDecider"/>. Replaying the events alone, without the contour, yields the same
/// <see cref="Position"/> the interpreter's start and apply produce, because every step entered
/// carries its kind. The one exception is <see cref="JudgmentRespawned"/>: a resumed judgment that
/// paused without an answer spawns again on the same visit, a position the interpreter never
/// produces, so the spawns count one more than the interpreter's entries alone would while the
/// visits stay as they were. The store snapshots this class inline, so it is also the document the
/// list and show queries read.
/// </summary>
public sealed class Flow
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public Guid ParcelId { get; private set; }
    [JsonInclude]
    public string ContourName { get; private set; } = string.Empty;
    /// <summary>Not called <c>Version</c>: the store gives an aggregate property of that name the stream's event count.</summary>
    [JsonInclude]
    public int ContourVersion { get; private set; }
    [JsonInclude]
    public string ContourHash { get; private set; } = string.Empty;
    [JsonInclude]
    public string StartedBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset StartedAt { get; private set; }
    [JsonInclude]
    public DateTimeOffset UpdatedAt { get; private set; }
    [JsonInclude]
    public FlowStatus Status { get; private set; }
    [JsonInclude]
    public string CurrentStep { get; private set; } = string.Empty;
    [JsonInclude]
    public Dictionary<string, int> Visits { get; private set; } = new(StringComparer.Ordinal);
    [JsonInclude]
    public int Spawns { get; private set; }
    [JsonInclude]
    public List<FlowStepEntry> Steps { get; private set; } = [];
    [JsonInclude]
    public string? PauseReason { get; private set; }
    [JsonInclude]
    public string? Lever { get; private set; }
    [JsonInclude]
    public string? Outcome { get; private set; }
    [JsonInclude]
    public string? StopReason { get; private set; }

    /// <summary>The flow whose end started this one, or null when nothing did.</summary>
    [JsonInclude]
    public Guid? ParentFlowId { get; private set; }

    /// <summary>
    /// The first flow of this one's chain; a flow no follow started is its own root. A flow
    /// recorded before chains existed has no root stored, and reads as its own root too.
    /// </summary>
    [JsonInclude]
    public Guid RootFlowId
    {
        get => rootFlowId == Guid.Empty ? Id : rootFlowId;
        private set => rootFlowId = value;
    }

    private Guid rootFlowId;

    /// <summary>How many follows lie between this flow and its root: zero for the root.</summary>
    [JsonInclude]
    public int Depth { get; private set; }

    /// <summary>What the judgments that paused the flow without an answer reported spending, in US dollars.</summary>
    [JsonInclude]
    public decimal PausedCost { get; private set; }

    /// <summary>
    /// What the flow has recorded spending, in US dollars: the cost of every answered judgment and
    /// the usage every paused judgment reported. It is read from the recorded visits rather than
    /// kept as a running total, so a flow recorded before costs were totalled counts its spend too.
    /// </summary>
    [JsonIgnore]
    public decimal Cost => Steps.Sum(entry => entry.Measurement?.CostUsd ?? 0m) + PausedCost;

    /// <summary>The usage the judgment that paused the flow reported, or null when the pause was not a judgment's.</summary>
    [JsonInclude]
    public Measurement? PauseUsage { get; private set; }

    /// <summary>Running or paused: the flow still holds its parcel.</summary>
    public bool IsLive => Status is FlowStatus.Running or FlowStatus.Paused;

    /// <summary>The step entered last, which is the one an answer or a result is recorded against.</summary>
    [JsonIgnore]
    public FlowStepEntry? Current => Steps.Count == 0 ? null : Steps[^1];

    /// <summary>Where the flow stands, as the interpreter reads it.</summary>
    [JsonIgnore]
    public FlowPosition Position => new(CurrentStep, Visits.ToImmutableDictionary(StringComparer.Ordinal), Spawns);

    public static Flow Create(FlowStarted started)
    {
        var flow = new Flow();
        flow.Apply(started);
        return flow;
    }

    public void Apply(FlowStarted started)
    {
        Id = started.FlowId;
        ParcelId = started.ParcelId;
        ContourName = started.ContourName;
        ContourVersion = started.ContourVersion;
        ContourHash = started.ContourHash;
        StartedBy = started.StartedBy;
        StartedAt = started.StartedAt;
        ParentFlowId = started.ParentFlowId;
        RootFlowId = started.RootFlowId ?? started.FlowId;
        Depth = started.Depth;
        Status = FlowStatus.Running;
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
        UpdatedAt = answered.AnsweredAt;
    }

    /// <summary>One more spawn on the current visit, and no visit, since the step was not entered again.</summary>
    public void Apply(JudgmentRespawned respawned)
    {
        Spawns += 1;
        Record(entry => entry with { RespawnedAt = respawned.RespawnedAt, SpawnedAt = null });
        UpdatedAt = respawned.RespawnedAt;
    }

    /// <summary>
    /// The current visit launched its agent on the spawn it last counted; nothing more is counted.
    /// The launch performs the resume the visit held, so a restart that launches it again is not a
    /// resume. The visit holds the launched agent's hash and model spec.
    /// </summary>
    public void Apply(JudgmentSpawned spawned)
    {
        Record(entry => entry with
        {
            SpawnedAt = spawned.SpawnedAt,
            ResumedAt = null,
            AgentHash = spawned.AgentHash,
            Model = spawned.Model,
        });
        UpdatedAt = spawned.SpawnedAt;
    }

    public void Apply(ActionCompleted completed)
    {
        Record(entry => entry with { Result = completed.Result, RecordedAt = completed.CompletedAt, ResumedAt = null });
        UpdatedAt = completed.CompletedAt;
    }

    public void Apply(FlowPaused paused)
    {
        Status = FlowStatus.Paused;
        PauseReason = paused.Reason;
        Lever = paused.Lever;
        PauseUsage = paused.Usage;
        PausedCost += paused.Usage?.CostUsd ?? 0m;
        UpdatedAt = paused.PausedAt;
    }

    public void Apply(FlowResumed resumed)
    {
        Status = FlowStatus.Running;
        PauseReason = null;
        Lever = null;
        PauseUsage = null;
        Record(entry => entry with { ResumedAt = resumed.ResumedAt, RespawnedAt = null });
        UpdatedAt = resumed.ResumedAt;
    }

    public void Apply(FlowEnded ended)
    {
        Status = FlowStatus.Ended;
        Outcome = ended.Outcome;
        UpdatedAt = ended.EndedAt;
    }

    public void Apply(FlowStopped stopped)
    {
        Status = FlowStatus.Stopped;
        StopReason = stopped.Reason;
        UpdatedAt = stopped.StoppedAt;
    }

    /// <summary>One more visit to the step entered, and one more spawn when it is a judgment, as the interpreter counts them.</summary>
    private void Enter(string step, StepKind kind, string performer, DateTimeOffset at)
    {
        CurrentStep = step;
        Visits[step] = Visits.GetValueOrDefault(step) + 1;
        Spawns += kind == StepKind.Judgment ? 1 : 0;
        Steps.Add(new FlowStepEntry { Step = step, Kind = kind, Performer = performer, StartedAt = at });
        UpdatedAt = at;
    }

    private void Record(Func<FlowStepEntry, FlowStepEntry> change)
    {
        if (Steps.Count > 0)
        {
            Steps[^1] = change(Steps[^1]);
        }
    }
}
