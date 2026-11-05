using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Types;

namespace Coanda.Core.FlowRuns;

/// <summary>
/// The rules of a flow run's record, as pure decisions: given the flow, the flow run (or the parcel, to
/// start one), and an intent, either the events that record it or a <see cref="DomainException"/>
/// quoting the rule. A flow run is live while it runs or is paused, and a parcel has at most one live
/// flow run; starting, ending, and stopping therefore also decide the parcel event that records it.
/// </summary>
public static class FlowRunDecider
{
    public static (FlowRunStarted Started, ParcelFlowRunAttached Attached) Start(
        Flow flow, int flowVersion, Parcel parcel, string startedBy, DateTimeOffset now)
    {
        if (parcel.IsClosed)
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is closed ({parcel.Resolution}); a flow run starts only on an open parcel.");
        }

        if (!flow.Accepts.Any(type => DefinitionResolution.SameName(type, parcel.TypeName)))
        {
            throw new DomainException(
                $"Flow '{flow.Name}' accepts parcels of type {string.Join(", ", flow.Accepts)}, not '{parcel.TypeName}'.");
        }

        if (parcel.LiveFlowRunId is Guid live)
        {
            throw new DomainException(
                $"A parcel has at most one live flow run; parcel {Ids.Short(parcel.Id)} already has flow run {Ids.Short(live)}, which must end or be stopped first.");
        }

        FlowStep first = flow.First
            ?? throw new DomainException($"Flow '{flow.Name}' has no steps, so a flow run has nowhere to start.");
        Guid flowRunId = Ids.New();
        var started = new FlowRunStarted(
            flowRunId, parcel.Id, flow.Name, flowVersion, flow.Hash, startedBy, first.Name, KindOf(first), PerformerOf(first), now);
        return (started, new ParcelFlowRunAttached(parcel.Id, flowRunId, now));
    }

    /// <summary>
    /// Starts the next link of a chain: a flow run of a flow whose follow matches the ended
    /// <paramref name="parent"/>, on the parent's parcel, recording the parent, the chain's root,
    /// and a depth one greater than the parent's. Every rule of <see cref="Start"/> holds too.
    /// </summary>
    public static (FlowRunStarted Started, ParcelFlowRunAttached Attached) Follow(
        Flow flow, int flowVersion, Parcel parcel, FlowRun parent, string startedBy, DateTimeOffset now)
    {
        FlowFollow follow = flow.Follow
            ?? throw new DomainException($"Flow '{flow.Name}' declares no follow, so no flow run's end starts it.");

        if (parent.Status != FlowRunStatus.Ended)
        {
            throw new DomainException(
                $"Flow run {Ids.Short(parent.Id)} {Describe(parent.Status)}; a follow starts only from a flow run that reached its end.");
        }

        if (parent.ParcelId != parcel.Id)
        {
            throw new DomainException(
                $"Flow run {Ids.Short(parent.Id)} ran on parcel {Ids.Short(parent.ParcelId)}; a follow runs on the parcel of the flow run it follows, not {Ids.Short(parcel.Id)}.");
        }

        bool outcomeMatches = string.Equals(parent.Outcome, follow.Outcome, StringComparison.Ordinal);
        bool flowMatches = follow.Flow is null || DefinitionResolution.SameName(parent.FlowName, follow.Flow);
        if (!outcomeMatches || !flowMatches)
        {
            throw new DomainException(
                $"Flow '{flow.Name}' follows the outcome '{follow.Outcome}'{(follow.Flow is null ? string.Empty : $" of flow '{follow.Flow}'")}; flow run {Ids.Short(parent.Id)} of flow '{parent.FlowName}' ended with '{parent.Outcome}'.");
        }

        (FlowRunStarted started, ParcelFlowRunAttached attached) = Start(flow, flowVersion, parcel, startedBy, now);
        return (started with { ParentFlowRunId = parent.Id, RootFlowRunId = parent.RootFlowRunId, Depth = parent.Depth + 1 }, attached);
    }

    public static StepStarted StartStep(Flow flow, FlowRun flowRun, string step, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "start a step");

        FlowStep entered = flow.Step(step)
            ?? throw new DomainException($"Flow '{flow.Name}' has no step '{step}'; a flow run enters only a step its flow has.");
        return new StepStarted(flowRun.Id, entered.Name, KindOf(entered), PerformerOf(entered), now);
    }

    /// <summary>
    /// Records the answer the session <paramref name="sessionId"/> gave for the current agent step
    /// step. Only the session the visit currently expects may answer it: the one its latest launch
    /// returned, with no resume since that launch.
    /// </summary>
    public static AgentStepAnswered Answer(
        Flow flow,
        FlowRun flowRun,
        string step,
        string agent,
        Guid sessionId,
        string agentHash,
        ModelSpec model,
        JsonElement answer,
        Measurement measurement,
        DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "record an answer");
        FlowRunStepEntry current = RequireCurrent(flowRun, step, "an answer");

        if (current.Kind != StepKind.AgentStep)
        {
            throw new DomainException($"Step '{current.Step}' is a system step; it takes a result, not an answer.");
        }

        if (!string.Equals(current.Performer, agent, StringComparison.Ordinal))
        {
            throw new DomainException($"Step '{current.Step}' names the agent '{current.Performer}'; an answer from '{agent}' is not its answer.");
        }

        if (string.IsNullOrWhiteSpace(agentHash))
        {
            throw new DomainException("An answer records the content hash of the agent that gave it; none was given.");
        }

        if (string.IsNullOrWhiteSpace(model.Vendor) || string.IsNullOrWhiteSpace(model.Model) || string.IsNullOrWhiteSpace(model.Effort))
        {
            throw new DomainException("An answer records the model spec it ran on: a vendor, a model, and an effort.");
        }

        RequireMeasurement(measurement);
        RequireExpected(current, sessionId, "an answer");

        return new AgentStepAnswered(flowRun.Id, current.Step, agent, agentHash.Trim(), model, answer.Clone(), measurement, now);
    }

    /// <summary>
    /// Counts one more spawn for a resumed agent step whose visit paused without an answer, which
    /// is performed again on the same visit and adds no visit. It is counted once per resume, and
    /// never past the flow's spawn cap; a visit's first spawn is counted when the step is entered.
    /// </summary>
    public static AgentStepRespawned Respawn(Flow flow, FlowRun flowRun, string step, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "spawn its agent step again");
        FlowRunStepEntry current = RequireCurrent(flowRun, step, "a spawn");

        if (current.Kind != StepKind.AgentStep)
        {
            throw new DomainException($"Step '{current.Step}' is a system step; only an agent step spawns an agent.");
        }

        if (current.ResumedAt is null)
        {
            throw new DomainException(
                $"Step '{current.Step}' has not been resumed since it was entered; its visit's spawn was counted when the flow run entered it.");
        }

        if (current.RespawnedAt is not null)
        {
            throw new DomainException(
                $"Step '{current.Step}' already counted a spawn for its latest resume; a resume spawns its agent step again once.");
        }

        if (current.SpawnedAt is null)
        {
            throw new DomainException(
                $"Step '{current.Step}' has not launched its agent on the spawn it last counted; a resume uses that spawn before it counts another.");
        }

        if (flowRun.Spawns >= flow.SpawnCap)
        {
            throw new DomainException(
                $"Flow '{flow.Name}' reached its spawn cap of {flow.SpawnCap}; performing agent step '{current.Step}' again would spawn another agent.");
        }

        return new AgentStepRespawned(flowRun.Id, current.Step, current.Performer, now);
    }

    /// <summary>
    /// Records that the current agent step launched its agent, the definition whose content hash
    /// is <paramref name="agentHash"/> on the model spec <paramref name="model"/>, on the spawn its
    /// visit last counted. A restart that launches the same visit again records it again, counting nothing.
    /// </summary>
    public static AgentStepSpawned Spawn(Flow flow, FlowRun flowRun, string step, string agentHash, ModelSpec model, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "launch its agent step");
        FlowRunStepEntry current = RequireCurrent(flowRun, step, "a launch");

        if (current.Kind != StepKind.AgentStep)
        {
            throw new DomainException($"Step '{current.Step}' is a system step; only an agent step spawns an agent.");
        }

        if (string.IsNullOrWhiteSpace(agentHash))
        {
            throw new DomainException("A launch records the content hash of the agent it launches; none was given.");
        }

        if (model is null || string.IsNullOrWhiteSpace(model.Vendor) || string.IsNullOrWhiteSpace(model.Model) || string.IsNullOrWhiteSpace(model.Effort))
        {
            throw new DomainException("A launch records the model spec its agent runs on: a vendor, a model, and an effort.");
        }

        return new AgentStepSpawned(flowRun.Id, current.Step, current.Performer, agentHash.Trim(), model, now, Ids.New());
    }

    /// <summary>
    /// Records the session the executor returned for the current agent step's latest launch,
    /// once per launch: a launch must be recorded first, and a later launch or a resume's counted
    /// spawn clears the session it held.
    /// </summary>
    public static AgentStepSessionOpened OpenSession(Flow flow, FlowRun flowRun, string step, Guid sessionId, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "record its agent step's session");
        FlowRunStepEntry current = RequireCurrent(flowRun, step, "a session");

        if (current.Kind != StepKind.AgentStep)
        {
            throw new DomainException($"Step '{current.Step}' is a system step; only an agent step runs an agent's session.");
        }

        if (current.SpawnedAt is null)
        {
            throw new DomainException(
                $"Step '{current.Step}' has not recorded the launch of its agent on the spawn it last counted; a session belongs to a recorded launch.");
        }

        if (current.SessionId is Guid held)
        {
            throw new DomainException(
                $"Step '{current.Step}' already holds session {Ids.Short(held)} for its latest launch; a launch returns one session.");
        }

        return sessionId == Guid.Empty
            ? throw new DomainException("A session is recorded by its id; none was given.")
            : new AgentStepSessionOpened(flowRun.Id, current.Step, sessionId, now);
    }

    public static SystemStepCompleted Complete(Flow flow, FlowRun flowRun, string step, string systemStep, JsonElement result, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "record a result");
        FlowRunStepEntry current = RequireCurrent(flowRun, step, "a result");

        if (current.Kind != StepKind.SystemStep)
        {
            throw new DomainException($"Step '{current.Step}' is an agent step; it takes an answer, not a result.");
        }

        if (!string.Equals(current.Performer, systemStep, StringComparison.Ordinal))
        {
            throw new DomainException($"Step '{current.Step}' names the system step '{current.Performer}'; a result from '{systemStep}' is not its result.");
        }

        return new SystemStepCompleted(flowRun.Id, current.Step, systemStep, result.Clone(), now);
    }

    /// <summary>
    /// Records the id the current system step is dispatched under, minted here, before the daemon
    /// calls the step. A visit that holds a dispatch with no outcome and no resume since is not
    /// dispatched under a new id: the daemon asks that dispatch's status instead.
    /// </summary>
    public static SystemStepDispatched Dispatch(Flow flow, FlowRun flowRun, string step, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "dispatch a system step");
        FlowRunStepEntry current = RequireCurrent(flowRun, step, "a dispatch");

        if (current.Kind != StepKind.SystemStep)
        {
            throw new DomainException($"Step '{current.Step}' is an agent step; only a system step is dispatched.");
        }

        if (current is { AwaitsDispatch: true, DispatchId: Guid held })
        {
            throw new DomainException(
                $"Step '{current.Step}' holds dispatch {Ids.Short(held)}, which has no outcome and has not been resumed since; its status is asked, and it is not dispatched under a new id.");
        }

        return new SystemStepDispatched(flowRun.Id, current.Step, current.Performer, Ids.New(), now);
    }

    /// <summary>
    /// Records the result the current system step's dispatch <paramref name="dispatchId"/> finished
    /// with, as that dispatch's outcome: the flow run is running, and the dispatch is the visit's
    /// latest, holds no outcome yet, and has not been replaced by a resume.
    /// </summary>
    public static SystemStepCompleted CompleteDispatch(Flow flow, FlowRun flowRun, Guid dispatchId, JsonElement result, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "record a dispatch's outcome");
        FlowRunStepEntry current = RequireDispatch(flowRun, dispatchId);
        return new SystemStepCompleted(flowRun.Id, current.Step, current.Performer, result.Clone(), now, dispatchId);
    }

    /// <summary>
    /// Pauses the flow run as the outcome of the current system step's dispatch
    /// <paramref name="dispatchId"/>, under every rule of <see cref="CompleteDispatch"/> and of a pause.
    /// </summary>
    public static FlowRunPaused PauseDispatch(
        Flow flow, FlowRun flowRun, Guid dispatchId, string reason, string lever, string pausedBy, DateTimeOffset now)
    {
        FlowRunPaused paused = Pause(flow, flowRun, reason, lever, pausedBy, now);
        RequireDispatch(flowRun, dispatchId);
        return paused with { DispatchId = dispatchId };
    }

    /// <summary>
    /// Records what an agent step on the current step spent when neither its answer nor its pause was
    /// recorded: a person paused or stopped the flow run while it ran, or its visit no longer expects
    /// the session <paramref name="sessionId"/>. A running, paused, or stopped flow run accepts it, and
    /// it changes nothing but the flow run's cost.
    /// </summary>
    public static AgentStepUsageRecorded RecordUsage(
        Flow flow, FlowRun flowRun, string step, string agent, Guid sessionId, Measurement usage, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);

        if (flowRun.Status is not (FlowRunStatus.Running or FlowRunStatus.Paused or FlowRunStatus.Stopped))
        {
            throw new DomainException(
                $"Flow run {Ids.Short(flowRun.Id)} {Describe(flowRun.Status)}; an agent step's usage is recorded on a running, paused, or stopped flow run.");
        }

        FlowRunStepEntry current = flowRun.Current
            ?? throw new DomainException($"Flow run {Ids.Short(flowRun.Id)} has entered no step, so an agent step's usage has nowhere to go.");

        if (!string.Equals(current.Step, step, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Flow run {Ids.Short(flowRun.Id)} is at step '{current.Step}'; an agent step's usage is recorded only for the current step, not '{step}'.");
        }

        if (current.Kind != StepKind.AgentStep)
        {
            throw new DomainException($"Step '{current.Step}' is a system step; only an agent step reports usage.");
        }

        if (!string.Equals(current.Performer, agent, StringComparison.Ordinal))
        {
            throw new DomainException($"Step '{current.Step}' names the agent '{current.Performer}'; usage from '{agent}' is not its usage.");
        }

        if (sessionId == Guid.Empty)
        {
            throw new DomainException("An agent step's usage is recorded with the session that reported it; none was given.");
        }

        RequireMeasurement(usage);

        return new AgentStepUsageRecorded(flowRun.Id, current.Step, agent, sessionId, usage, now);
    }

    /// <summary>
    /// Pauses a running flow run; <paramref name="usage"/> is what an agent step that paused without an
    /// answer reported. When the agent step's <paramref name="session"/> is given, the current visit
    /// must still expect that session, as it must for an answer.
    /// </summary>
    public static FlowRunPaused Pause(
        Flow flow,
        FlowRun flowRun,
        string reason,
        string lever,
        string pausedBy,
        DateTimeOffset now,
        Measurement? usage = null,
        Guid? session = null)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "pause it");

        if (session is Guid sessionId)
        {
            FlowRunStepEntry current = flowRun.Current
                ?? throw new DomainException($"Flow run {Ids.Short(flowRun.Id)} has entered no step, so no agent step's session can pause it.");
            RequireExpected(current, sessionId, "a pause");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A pause records why the flow run stopped; give a reason.");
        }

        if (string.IsNullOrWhiteSpace(lever))
        {
            throw new DomainException("A pause records the lever a person acts on to resume the flow run; give a lever.");
        }

        if (usage is not null)
        {
            RequireMeasurement(usage);
        }

        return new FlowRunPaused(flowRun.Id, reason.Trim(), lever.Trim(), pausedBy, now, usage);
    }

    public static FlowRunResumed Resume(Flow flow, FlowRun flowRun, string? note, string resumedBy, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);

        if (flowRun.Status != FlowRunStatus.Paused)
        {
            throw new DomainException($"Flow run {Ids.Short(flowRun.Id)} {Describe(flowRun.Status)}; only a paused flow run resumes.");
        }

        return new FlowRunResumed(flowRun.Id, resumedBy, note?.Trim() ?? string.Empty, now);
    }

    public static (FlowRunEnded Ended, ParcelFlowRunReleased Released) End(
        Flow flow, FlowRun flowRun, string outcome, string endedBy, DateTimeOffset now)
    {
        RequireFlow(flow, flowRun);
        RequireRunning(flowRun, "end it");

        if (!string.Equals(outcome, flow.Outcome, StringComparison.Ordinal))
        {
            throw new DomainException($"Flow '{flow.Name}' ends with its declared outcome '{flow.Outcome}', not '{outcome}'.");
        }

        return (new FlowRunEnded(flowRun.Id, outcome, endedBy, now), new ParcelFlowRunReleased(flowRun.ParcelId, flowRun.Id, now));
    }

    /// <summary>
    /// The close a flow run's end records beside its release when its flow declares a closing
    /// resolution, or null when it declares none. It is decided against <paramref name="released"/>,
    /// the parcel as the end's release leaves it, by every rule of <see cref="ParcelDecider.Close"/>,
    /// the gates the type binds to a close among them; <paramref name="closedBy"/> is the flow run's own
    /// principal, and the reason names the flow run and its flow. A refusal quotes the close's rule.
    /// </summary>
    public static ParcelClosed? CloseOnEnd(
        Flow flow, FlowRun flowRun, ParcelType type, GateSet gates, Parcel released, string closedBy, DateTimeOffset now)
    {
        if (flow.Closes is not { } resolution)
        {
            return null;
        }

        if (released.Id != flowRun.ParcelId)
        {
            throw new DomainException(
                $"Flow run {Ids.Short(flowRun.Id)} runs on parcel {Ids.Short(flowRun.ParcelId)}; its end closes that parcel, not {Ids.Short(released.Id)}.");
        }

        string reason =
            $"Flow run {Ids.Short(flowRun.Id)} of flow '{flow.Name}' reached its end, and the flow closes its parcel as '{resolution}'.";
        try
        {
            return ParcelDecider.Close(type, gates, released, resolution, reason, closedBy, now);
        }
        catch (DomainException refused)
        {
            throw new DomainException(
                $"Flow run {Ids.Short(flowRun.Id)} of flow '{flow.Name}' reached its end, but its parcel could not be closed as '{resolution}', so neither the end nor the close was recorded: {refused.Message}");
        }
    }

    /// <summary>Stops a live flow run, running or paused, so that a pause nobody resumes does not hold its parcel forever.</summary>
    public static (FlowRunStopped Stopped, ParcelFlowRunReleased Released) Stop(FlowRun flowRun, string reason, string stoppedBy, DateTimeOffset now)
    {
        if (!flowRun.IsLive)
        {
            throw new DomainException($"Flow run {Ids.Short(flowRun.Id)} {Describe(flowRun.Status)}; only a live flow run stops.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Stopping a flow run records why; give a reason.");
        }

        return (new FlowRunStopped(flowRun.Id, stoppedBy, reason.Trim(), now), new ParcelFlowRunReleased(flowRun.ParcelId, flowRun.Id, now));
    }

    private static void RequireMeasurement(Measurement measurement)
    {
        if (measurement.Tokens < 0 || measurement.CostUsd < 0 || measurement.Turns < 0 || measurement.Duration < TimeSpan.Zero)
        {
            throw new DomainException("A measurement is never negative: tokens, cost, turns, and duration are zero or more.");
        }
    }

    /// <summary>Refuses what a session reports unless the visit still expects that session.</summary>
    private static void RequireExpected(FlowRunStepEntry current, Guid sessionId, string what)
    {
        if (current.Expects(sessionId))
        {
            return;
        }

        string expected = current switch
        {
            { SessionId: Guid held, ResumedAt: not null } => $"was resumed after it launched session {Ids.Short(held)}, so the resume performs it again with a new spawn",
            { SessionId: Guid held } => $"expects session {Ids.Short(held)}",
            _ => "holds no session for its latest launch",
        };
        throw new DomainException(
            $"Step '{current.Step}' {expected}; {what} from session {Ids.Short(sessionId)} is not recorded on this visit.");
    }

    /// <summary>
    /// The current visit, when it takes an outcome for <paramref name="dispatchId"/>; otherwise a
    /// refusal naming why: the visit holds another dispatch or none, the dispatch has its outcome, or
    /// a resume has replaced it.
    /// </summary>
    private static FlowRunStepEntry RequireDispatch(FlowRun flowRun, Guid dispatchId)
    {
        FlowRunStepEntry current = flowRun.Current
            ?? throw new DomainException($"Flow run {Ids.Short(flowRun.Id)} has entered no step, so dispatch {Ids.Short(dispatchId)} has no outcome to record.");

        if (current.ExpectsDispatch(dispatchId))
        {
            return current;
        }

        string refusal = current switch
        {
            { Kind: StepKind.AgentStep } => "is an agent step, which is not dispatched",
            { DispatchId: null } => "holds no dispatch",
            { DispatchId: Guid held } when held != dispatchId => $"holds dispatch {Ids.Short(held)}, not {Ids.Short(dispatchId)}",
            { IsRecorded: true } or { DispatchOutcomeAt: not null } => $"already holds the outcome of dispatch {Ids.Short(dispatchId)}",
            _ => $"was resumed after dispatch {Ids.Short(dispatchId)}, so the resume dispatches it again under a new id",
        };
        throw new DomainException(
            $"Step '{current.Step}' of flow run {Ids.Short(flowRun.Id)} {refusal}; an outcome under dispatch {Ids.Short(dispatchId)} is not recorded.");
    }

    private static void RequireFlow(Flow flow, FlowRun flowRun)
    {
        if (!string.Equals(flow.Hash, flowRun.FlowHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Flow run {Ids.Short(flowRun.Id)} runs '{flowRun.FlowName}@{flowRun.FlowHash}', not '{flow.Name}@{flow.Hash}'; a flow run keeps the flow it started under.");
        }
    }

    private static void RequireRunning(FlowRun flowRun, string intent)
    {
        if (flowRun.Status != FlowRunStatus.Running)
        {
            throw new DomainException(
                $"Flow run {Ids.Short(flowRun.Id)} {Describe(flowRun.Status)}; only a running flow run can {intent}.");
        }
    }

    private static FlowRunStepEntry RequireCurrent(FlowRun flowRun, string step, string what)
    {
        FlowRunStepEntry current = flowRun.Current
            ?? throw new DomainException($"Flow run {Ids.Short(flowRun.Id)} has entered no step, so {what} has nowhere to go.");

        if (!string.Equals(current.Step, step, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Flow run {Ids.Short(flowRun.Id)} is at step '{current.Step}'; {what} is recorded only for the current step, not '{step}'.");
        }

        if (current.IsRecorded)
        {
            throw new DomainException(
                $"Step '{current.Step}' already holds what this visit recorded; the flow run enters the step again before it records another.");
        }

        return current;
    }

    private static string Describe(FlowRunStatus status) => status switch
    {
        FlowRunStatus.Running => "is running",
        FlowRunStatus.Paused => "is paused",
        FlowRunStatus.Ended => "has ended",
        FlowRunStatus.Stopped => "has been stopped",
        _ => $"is {status}",
    };

    private static StepKind KindOf(FlowStep step) => step.IsAgentStep ? StepKind.AgentStep : StepKind.SystemStep;

    private static string PerformerOf(FlowStep step) => (step.IsAgentStep ? step.Agent : step.SystemStep) ?? string.Empty;
}
