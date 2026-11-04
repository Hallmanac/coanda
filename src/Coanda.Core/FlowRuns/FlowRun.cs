using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;

namespace Coanda.Core.FlowRuns;

/// <summary>
/// One execution of a flow on a parcel. State changes only through <c>Apply</c>; the rules live in
/// <see cref="FlowRunDecider"/>. Replaying the events alone, without the flow, yields the same
/// <see cref="Position"/> the interpreter's start and apply produce, because every step entered
/// carries its kind. The one exception is <see cref="AgentStepRespawned"/>: a resumed agent step that
/// paused without an answer spawns again on the same visit, a position the interpreter never
/// produces, so the spawns count one more than the interpreter's entries alone would while the
/// visits stay as they were. The store snapshots this class inline, so it is also the document the
/// list and show queries read.
/// </summary>
public sealed class FlowRun
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
    public FlowRunStatus Status { get; private set; }
    [JsonInclude]
    public string CurrentStep { get; private set; } = string.Empty;
    [JsonInclude]
    public Dictionary<string, int> Visits { get; private set; } = new(StringComparer.Ordinal);
    [JsonInclude]
    public int Spawns { get; private set; }
    [JsonInclude]
    public List<FlowRunStepEntry> Steps { get; private set; } = [];
    [JsonInclude]
    public string? PauseReason { get; private set; }
    [JsonInclude]
    public string? Lever { get; private set; }
    [JsonInclude]
    public string? Outcome { get; private set; }
    [JsonInclude]
    public string? StopReason { get; private set; }

    /// <summary>The flow run whose end started this one, or null when nothing did.</summary>
    [JsonInclude]
    public Guid? ParentFlowRunId { get; private set; }

    /// <summary>
    /// The first flow run of this one's chain; a flow run no follow started is its own root. A flow run
    /// recorded before chains existed has no root stored, and reads as its own root too.
    /// </summary>
    [JsonInclude]
    public Guid RootFlowRunId
    {
        get => rootFlowRunId == Guid.Empty ? Id : rootFlowRunId;
        private set => rootFlowRunId = value;
    }

    private Guid rootFlowRunId;

    /// <summary>How many follows lie between this flow run and its root: zero for the root.</summary>
    [JsonInclude]
    public int Depth { get; private set; }

    /// <summary>What the agent steps that paused the flow run without an answer reported spending, in US dollars.</summary>
    [JsonInclude]
    public decimal PausedCost { get; private set; }

    /// <summary>
    /// What the agent steps whose answer or pause was not recorded reported spending, in US dollars:
    /// those a person's pause or stop ended, and those whose visit no longer expected their session.
    /// </summary>
    [JsonInclude]
    public decimal UnrecordedCost { get; private set; }

    /// <summary>
    /// What the flow run has recorded spending, in US dollars: the cost of every answered agent step, the
    /// usage every paused agent step reported, and the usage of every agent step whose answer or pause
    /// was not recorded. It is read from the recorded visits rather than kept as a running total, so
    /// a flow run recorded before costs were totalled counts its spend too.
    /// </summary>
    [JsonIgnore]
    public decimal Cost => Steps.Sum(entry => entry.Measurement?.CostUsd ?? 0m) + PausedCost + UnrecordedCost;

    /// <summary>The usage the agent step that paused the flow run reported, or null when the pause was not an agent step's.</summary>
    [JsonInclude]
    public Measurement? PauseUsage { get; private set; }

    /// <summary>Running or paused: the flow run still holds its parcel.</summary>
    public bool IsLive => Status is FlowRunStatus.Running or FlowRunStatus.Paused;

    /// <summary>The step entered last, which is the one an answer or a result is recorded against.</summary>
    [JsonIgnore]
    public FlowRunStepEntry? Current => Steps.Count == 0 ? null : Steps[^1];

    /// <summary>
    /// Whether the flow run is running and its current visit still expects the launch
    /// <paramref name="launchId"/>: see <see cref="FlowRunStepEntry.ExpectsLaunch"/>. Once this is
    /// false for a launch it is never true again, since every later launch carries an id of its own.
    /// </summary>
    public bool ExpectsLaunch(Guid launchId) =>
        Status == FlowRunStatus.Running && Current is { } current && current.ExpectsLaunch(launchId);

    /// <summary>Where the flow run stands, as the interpreter reads it.</summary>
    [JsonIgnore]
    public FlowRunPosition Position => new(CurrentStep, Visits.ToImmutableDictionary(StringComparer.Ordinal), Spawns);

    public static FlowRun Create(FlowRunStarted started)
    {
        var flowRun = new FlowRun();
        flowRun.Apply(started);
        return flowRun;
    }

    public void Apply(FlowRunStarted started)
    {
        Id = started.FlowRunId;
        ParcelId = started.ParcelId;
        FlowName = started.FlowName;
        FlowVersion = started.FlowVersion;
        FlowHash = started.FlowHash;
        StartedBy = started.StartedBy;
        StartedAt = started.StartedAt;
        ParentFlowRunId = started.ParentFlowRunId;
        RootFlowRunId = started.RootFlowRunId ?? started.FlowRunId;
        Depth = started.Depth;
        Status = FlowRunStatus.Running;
        Enter(started.FirstStep, started.FirstStepKind, started.FirstStepPerformer, started.StartedAt);
    }

    public void Apply(StepStarted started) =>
        Enter(started.Step, started.Kind, started.Performer, started.StartedAt);

    public void Apply(AgentStepAnswered answered)
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
    public void Apply(AgentStepRespawned respawned)
    {
        Spawns += 1;
        Record(entry => entry with { RespawnedAt = respawned.RespawnedAt, SpawnedAt = null, SessionId = null, LaunchId = null });
        UpdatedAt = respawned.RespawnedAt;
    }

    /// <summary>
    /// The current visit launched its agent on the spawn it last counted; nothing more is counted.
    /// The launch performs the resume the visit held, so a restart that launches it again is not a
    /// resume. The visit holds the launched agent's hash and model spec, and no session until the
    /// executor returns one for this launch.
    /// </summary>
    public void Apply(AgentStepSpawned spawned)
    {
        Record(entry => entry with
        {
            SpawnedAt = spawned.SpawnedAt,
            ResumedAt = null,
            AgentHash = spawned.AgentHash,
            Model = spawned.Model,
            SessionId = null,
            LaunchId = spawned.LaunchId,
        });
        UpdatedAt = spawned.SpawnedAt;
    }

    /// <summary>The current visit's latest launch returned its session.</summary>
    public void Apply(AgentStepSessionOpened opened)
    {
        Record(entry => entry with { SessionId = opened.SessionId });
        UpdatedAt = opened.OpenedAt;
    }

    public void Apply(SystemStepCompleted completed)
    {
        Record(entry => entry with { Result = completed.Result, RecordedAt = completed.CompletedAt, ResumedAt = null });
        UpdatedAt = completed.CompletedAt;
    }

    public void Apply(FlowRunPaused paused)
    {
        Status = FlowRunStatus.Paused;
        PauseReason = paused.Reason;
        Lever = paused.Lever;
        PauseUsage = paused.Usage;
        PausedCost += paused.Usage?.CostUsd ?? 0m;
        UpdatedAt = paused.PausedAt;
    }

    /// <summary>Only what the agent step spent is kept; the pause, the visit, and the status stay as they are.</summary>
    public void Apply(AgentStepUsageRecorded recorded)
    {
        UnrecordedCost += recorded.Usage.CostUsd;
        UpdatedAt = recorded.RecordedAt;
    }

    public void Apply(FlowRunResumed resumed)
    {
        Status = FlowRunStatus.Running;
        PauseReason = null;
        Lever = null;
        PauseUsage = null;
        Record(entry => entry with { ResumedAt = resumed.ResumedAt, RespawnedAt = null });
        UpdatedAt = resumed.ResumedAt;
    }

    public void Apply(FlowRunEnded ended)
    {
        Status = FlowRunStatus.Ended;
        Outcome = ended.Outcome;
        UpdatedAt = ended.EndedAt;
    }

    public void Apply(FlowRunStopped stopped)
    {
        Status = FlowRunStatus.Stopped;
        StopReason = stopped.Reason;
        UpdatedAt = stopped.StoppedAt;
    }

    /// <summary>One more visit to the step entered, and one more spawn when it is an agent step, as the interpreter counts them.</summary>
    private void Enter(string step, StepKind kind, string performer, DateTimeOffset at)
    {
        CurrentStep = step;
        Visits[step] = Visits.GetValueOrDefault(step) + 1;
        Spawns += kind == StepKind.AgentStep ? 1 : 0;
        Steps.Add(new FlowRunStepEntry { Step = step, Kind = kind, Performer = performer, StartedAt = at });
        UpdatedAt = at;
    }

    private void Record(Func<FlowRunStepEntry, FlowRunStepEntry> change)
    {
        if (Steps.Count > 0)
        {
            Steps[^1] = change(Steps[^1]);
        }
    }
}
