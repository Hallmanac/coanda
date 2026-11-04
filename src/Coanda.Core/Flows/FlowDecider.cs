using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows.Events;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;

namespace Coanda.Core.Flows;

/// <summary>
/// The rules of a flow's record, as pure decisions: given the contour, the flow (or the parcel, to
/// start one), and an intent, either the events that record it or a <see cref="DomainException"/>
/// quoting the rule. A flow is live while it runs or is paused, and a parcel has at most one live
/// flow; starting, ending, and stopping therefore also decide the parcel event that records it.
/// </summary>
public static class FlowDecider
{
    public static (FlowStarted Started, ParcelFlowAttached Attached) Start(
        Contour contour, int contourVersion, Parcel parcel, string startedBy, DateTimeOffset now)
    {
        if (parcel.IsClosed)
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is closed ({parcel.Resolution}); a flow starts only on an open parcel.");
        }

        if (!contour.Accepts.Any(type => DefinitionResolution.SameName(type, parcel.TypeName)))
        {
            throw new DomainException(
                $"Contour '{contour.Name}' accepts parcels of type {string.Join(", ", contour.Accepts)}, not '{parcel.TypeName}'.");
        }

        if (parcel.LiveFlowId is Guid live)
        {
            throw new DomainException(
                $"A parcel has at most one live flow; parcel {Ids.Short(parcel.Id)} already has flow {Ids.Short(live)}, which must end or be stopped first.");
        }

        ContourStep first = contour.First
            ?? throw new DomainException($"Contour '{contour.Name}' has no steps, so a flow has nowhere to start.");
        Guid flowId = Ids.New();
        var started = new FlowStarted(
            flowId, parcel.Id, contour.Name, contourVersion, contour.Hash, startedBy, first.Name, KindOf(first), PerformerOf(first), now);
        return (started, new ParcelFlowAttached(parcel.Id, flowId, now));
    }

    /// <summary>
    /// Starts the next link of a chain: a flow of a contour whose follow matches the ended
    /// <paramref name="parent"/>, on the parent's parcel, recording the parent, the chain's root,
    /// and a depth one greater than the parent's. Every rule of <see cref="Start"/> holds too.
    /// </summary>
    public static (FlowStarted Started, ParcelFlowAttached Attached) Follow(
        Contour contour, int contourVersion, Parcel parcel, Flow parent, string startedBy, DateTimeOffset now)
    {
        ContourFollow follow = contour.Follow
            ?? throw new DomainException($"Contour '{contour.Name}' declares no follow, so no flow's end starts it.");

        if (parent.Status != FlowStatus.Ended)
        {
            throw new DomainException(
                $"Flow {Ids.Short(parent.Id)} {Describe(parent.Status)}; a follow starts only from a flow that reached its end.");
        }

        if (parent.ParcelId != parcel.Id)
        {
            throw new DomainException(
                $"Flow {Ids.Short(parent.Id)} ran on parcel {Ids.Short(parent.ParcelId)}; a follow runs on the parcel of the flow it follows, not {Ids.Short(parcel.Id)}.");
        }

        bool outcomeMatches = string.Equals(parent.Outcome, follow.Outcome, StringComparison.Ordinal);
        bool contourMatches = follow.Contour is null || DefinitionResolution.SameName(parent.ContourName, follow.Contour);
        if (!outcomeMatches || !contourMatches)
        {
            throw new DomainException(
                $"Contour '{contour.Name}' follows the outcome '{follow.Outcome}'{(follow.Contour is null ? string.Empty : $" of contour '{follow.Contour}'")}; flow {Ids.Short(parent.Id)} of contour '{parent.ContourName}' ended with '{parent.Outcome}'.");
        }

        (FlowStarted started, ParcelFlowAttached attached) = Start(contour, contourVersion, parcel, startedBy, now);
        return (started with { ParentFlowId = parent.Id, RootFlowId = parent.RootFlowId, Depth = parent.Depth + 1 }, attached);
    }

    public static StepStarted StartStep(Contour contour, Flow flow, string step, DateTimeOffset now)
    {
        RequireContour(contour, flow);
        RequireRunning(flow, "start a step");

        ContourStep entered = contour.Step(step)
            ?? throw new DomainException($"Contour '{contour.Name}' has no step '{step}'; a flow enters only a step its contour has.");
        return new StepStarted(flow.Id, entered.Name, KindOf(entered), PerformerOf(entered), now);
    }

    /// <summary>
    /// Records the answer the session <paramref name="sessionId"/> gave for the current judgment
    /// step. Only the session the visit currently expects may answer it: the one its latest launch
    /// returned, with no resume since that launch.
    /// </summary>
    public static JudgmentAnswered Answer(
        Contour contour,
        Flow flow,
        string step,
        string agent,
        Guid sessionId,
        string agentHash,
        ModelSpec model,
        JsonElement answer,
        Measurement measurement,
        DateTimeOffset now)
    {
        RequireContour(contour, flow);
        RequireRunning(flow, "record an answer");
        FlowStepEntry current = RequireCurrent(flow, step, "an answer");

        if (current.Kind != StepKind.Judgment)
        {
            throw new DomainException($"Step '{current.Step}' is an action step; it takes a result, not an answer.");
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

        return new JudgmentAnswered(flow.Id, current.Step, agent, agentHash.Trim(), model, answer.Clone(), measurement, now);
    }

    /// <summary>
    /// Counts one more spawn for a resumed judgment step whose visit paused without an answer, which
    /// is performed again on the same visit and adds no visit. It is counted once per resume, and
    /// never past the contour's spawn cap; a visit's first spawn is counted when the step is entered.
    /// </summary>
    public static JudgmentRespawned Respawn(Contour contour, Flow flow, string step, DateTimeOffset now)
    {
        RequireContour(contour, flow);
        RequireRunning(flow, "spawn its judgment again");
        FlowStepEntry current = RequireCurrent(flow, step, "a spawn");

        if (current.Kind != StepKind.Judgment)
        {
            throw new DomainException($"Step '{current.Step}' is an action step; only a judgment step spawns an agent.");
        }

        if (current.ResumedAt is null)
        {
            throw new DomainException(
                $"Step '{current.Step}' has not been resumed since it was entered; its visit's spawn was counted when the flow entered it.");
        }

        if (current.RespawnedAt is not null)
        {
            throw new DomainException(
                $"Step '{current.Step}' already counted a spawn for its latest resume; a resume spawns its judgment again once.");
        }

        if (current.SpawnedAt is null)
        {
            throw new DomainException(
                $"Step '{current.Step}' has not launched its agent on the spawn it last counted; a resume uses that spawn before it counts another.");
        }

        if (flow.Spawns >= contour.SpawnCap)
        {
            throw new DomainException(
                $"Contour '{contour.Name}' reached its spawn cap of {contour.SpawnCap}; performing judgment step '{current.Step}' again would spawn another agent.");
        }

        return new JudgmentRespawned(flow.Id, current.Step, current.Performer, now);
    }

    /// <summary>
    /// Records that the current judgment step launched its agent, the definition whose content hash
    /// is <paramref name="agentHash"/> on the model spec <paramref name="model"/>, on the spawn its
    /// visit last counted. A restart that launches the same visit again records it again, counting nothing.
    /// </summary>
    public static JudgmentSpawned Spawn(Contour contour, Flow flow, string step, string agentHash, ModelSpec model, DateTimeOffset now)
    {
        RequireContour(contour, flow);
        RequireRunning(flow, "launch its judgment");
        FlowStepEntry current = RequireCurrent(flow, step, "a launch");

        if (current.Kind != StepKind.Judgment)
        {
            throw new DomainException($"Step '{current.Step}' is an action step; only a judgment step spawns an agent.");
        }

        if (string.IsNullOrWhiteSpace(agentHash))
        {
            throw new DomainException("A launch records the content hash of the agent it launches; none was given.");
        }

        if (model is null || string.IsNullOrWhiteSpace(model.Vendor) || string.IsNullOrWhiteSpace(model.Model) || string.IsNullOrWhiteSpace(model.Effort))
        {
            throw new DomainException("A launch records the model spec its agent runs on: a vendor, a model, and an effort.");
        }

        return new JudgmentSpawned(flow.Id, current.Step, current.Performer, agentHash.Trim(), model, now);
    }

    /// <summary>
    /// Records the session the executor returned for the current judgment step's latest launch,
    /// once per launch: a launch must be recorded first, and a later launch or a resume's counted
    /// spawn clears the session it held.
    /// </summary>
    public static JudgmentSessionOpened OpenSession(Contour contour, Flow flow, string step, Guid sessionId, DateTimeOffset now)
    {
        RequireContour(contour, flow);
        RequireRunning(flow, "record its judgment's session");
        FlowStepEntry current = RequireCurrent(flow, step, "a session");

        if (current.Kind != StepKind.Judgment)
        {
            throw new DomainException($"Step '{current.Step}' is an action step; only a judgment step runs an agent's session.");
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
            : new JudgmentSessionOpened(flow.Id, current.Step, sessionId, now);
    }

    public static ActionCompleted Complete(Contour contour, Flow flow, string step, string action, JsonElement result, DateTimeOffset now)
    {
        RequireContour(contour, flow);
        RequireRunning(flow, "record a result");
        FlowStepEntry current = RequireCurrent(flow, step, "a result");

        if (current.Kind != StepKind.Action)
        {
            throw new DomainException($"Step '{current.Step}' is a judgment step; it takes an answer, not a result.");
        }

        if (!string.Equals(current.Performer, action, StringComparison.Ordinal))
        {
            throw new DomainException($"Step '{current.Step}' names the action '{current.Performer}'; a result from '{action}' is not its result.");
        }

        return new ActionCompleted(flow.Id, current.Step, action, result.Clone(), now);
    }

    /// <summary>
    /// Records what a judgment on the current step spent when neither its answer nor its pause was
    /// recorded: a person paused or stopped the flow while it ran, or its visit no longer expects
    /// the session <paramref name="sessionId"/>. A running, paused, or stopped flow accepts it, and
    /// it changes nothing but the flow's cost.
    /// </summary>
    public static JudgmentUsageRecorded RecordUsage(
        Contour contour, Flow flow, string step, string agent, Guid sessionId, Measurement usage, DateTimeOffset now)
    {
        RequireContour(contour, flow);

        if (flow.Status is not (FlowStatus.Running or FlowStatus.Paused or FlowStatus.Stopped))
        {
            throw new DomainException(
                $"Flow {Ids.Short(flow.Id)} {Describe(flow.Status)}; a judgment's usage is recorded on a running, paused, or stopped flow.");
        }

        FlowStepEntry current = flow.Current
            ?? throw new DomainException($"Flow {Ids.Short(flow.Id)} has entered no step, so a judgment's usage has nowhere to go.");

        if (!string.Equals(current.Step, step, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Flow {Ids.Short(flow.Id)} is at step '{current.Step}'; a judgment's usage is recorded only for the current step, not '{step}'.");
        }

        if (current.Kind != StepKind.Judgment)
        {
            throw new DomainException($"Step '{current.Step}' is an action step; only a judgment step reports usage.");
        }

        if (!string.Equals(current.Performer, agent, StringComparison.Ordinal))
        {
            throw new DomainException($"Step '{current.Step}' names the agent '{current.Performer}'; usage from '{agent}' is not its usage.");
        }

        if (sessionId == Guid.Empty)
        {
            throw new DomainException("A judgment's usage is recorded with the session that reported it; none was given.");
        }

        RequireMeasurement(usage);

        return new JudgmentUsageRecorded(flow.Id, current.Step, agent, sessionId, usage, now);
    }

    /// <summary>
    /// Pauses a running flow; <paramref name="usage"/> is what a judgment that paused without an
    /// answer reported. When the judgment's <paramref name="session"/> is given, the current visit
    /// must still expect that session, as it must for an answer.
    /// </summary>
    public static FlowPaused Pause(
        Contour contour,
        Flow flow,
        string reason,
        string lever,
        string pausedBy,
        DateTimeOffset now,
        Measurement? usage = null,
        Guid? session = null)
    {
        RequireContour(contour, flow);
        RequireRunning(flow, "pause it");

        if (session is Guid sessionId)
        {
            FlowStepEntry current = flow.Current
                ?? throw new DomainException($"Flow {Ids.Short(flow.Id)} has entered no step, so no judgment's session can pause it.");
            RequireExpected(current, sessionId, "a pause");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A pause records why the flow stopped; give a reason.");
        }

        if (string.IsNullOrWhiteSpace(lever))
        {
            throw new DomainException("A pause records the lever a person acts on to resume the flow; give a lever.");
        }

        if (usage is not null)
        {
            RequireMeasurement(usage);
        }

        return new FlowPaused(flow.Id, reason.Trim(), lever.Trim(), pausedBy, now, usage);
    }

    public static FlowResumed Resume(Contour contour, Flow flow, string? note, string resumedBy, DateTimeOffset now)
    {
        RequireContour(contour, flow);

        if (flow.Status != FlowStatus.Paused)
        {
            throw new DomainException($"Flow {Ids.Short(flow.Id)} {Describe(flow.Status)}; only a paused flow resumes.");
        }

        return new FlowResumed(flow.Id, resumedBy, note?.Trim() ?? string.Empty, now);
    }

    public static (FlowEnded Ended, ParcelFlowReleased Released) End(
        Contour contour, Flow flow, string outcome, string endedBy, DateTimeOffset now)
    {
        RequireContour(contour, flow);
        RequireRunning(flow, "end it");

        if (!string.Equals(outcome, contour.Outcome, StringComparison.Ordinal))
        {
            throw new DomainException($"Contour '{contour.Name}' ends with its declared outcome '{contour.Outcome}', not '{outcome}'.");
        }

        return (new FlowEnded(flow.Id, outcome, endedBy, now), new ParcelFlowReleased(flow.ParcelId, flow.Id, now));
    }

    /// <summary>Stops a live flow, running or paused, so that a pause nobody resumes does not hold its parcel forever.</summary>
    public static (FlowStopped Stopped, ParcelFlowReleased Released) Stop(Flow flow, string reason, string stoppedBy, DateTimeOffset now)
    {
        if (!flow.IsLive)
        {
            throw new DomainException($"Flow {Ids.Short(flow.Id)} {Describe(flow.Status)}; only a live flow stops.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Stopping a flow records why; give a reason.");
        }

        return (new FlowStopped(flow.Id, stoppedBy, reason.Trim(), now), new ParcelFlowReleased(flow.ParcelId, flow.Id, now));
    }

    private static void RequireMeasurement(Measurement measurement)
    {
        if (measurement.Tokens < 0 || measurement.CostUsd < 0 || measurement.Turns < 0 || measurement.Duration < TimeSpan.Zero)
        {
            throw new DomainException("A measurement is never negative: tokens, cost, turns, and duration are zero or more.");
        }
    }

    /// <summary>Refuses what a session reports unless the visit still expects that session.</summary>
    private static void RequireExpected(FlowStepEntry current, Guid sessionId, string what)
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

    private static void RequireContour(Contour contour, Flow flow)
    {
        if (!string.Equals(contour.Hash, flow.ContourHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Flow {Ids.Short(flow.Id)} runs '{flow.ContourName}@{flow.ContourHash}', not '{contour.Name}@{contour.Hash}'; a flow keeps the contour it started under.");
        }
    }

    private static void RequireRunning(Flow flow, string intent)
    {
        if (flow.Status != FlowStatus.Running)
        {
            throw new DomainException(
                $"Flow {Ids.Short(flow.Id)} {Describe(flow.Status)}; only a running flow can {intent}.");
        }
    }

    private static FlowStepEntry RequireCurrent(Flow flow, string step, string what)
    {
        FlowStepEntry current = flow.Current
            ?? throw new DomainException($"Flow {Ids.Short(flow.Id)} has entered no step, so {what} has nowhere to go.");

        if (!string.Equals(current.Step, step, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Flow {Ids.Short(flow.Id)} is at step '{current.Step}'; {what} is recorded only for the current step, not '{step}'.");
        }

        if (current.IsRecorded)
        {
            throw new DomainException(
                $"Step '{current.Step}' already holds what this visit recorded; the flow enters the step again before it records another.");
        }

        return current;
    }

    private static string Describe(FlowStatus status) => status switch
    {
        FlowStatus.Running => "is running",
        FlowStatus.Paused => "is paused",
        FlowStatus.Ended => "has ended",
        FlowStatus.Stopped => "has been stopped",
        _ => $"is {status}",
    };

    private static StepKind KindOf(ContourStep step) => step.IsJudgment ? StepKind.Judgment : StepKind.Action;

    private static string PerformerOf(ContourStep step) => (step.IsJudgment ? step.Agent : step.Action) ?? string.Empty;
}
