using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flights.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;

namespace Coanda.Core.Flights;

/// <summary>
/// The rules of a flight's record, as pure decisions: given the flow, the flight (or the parcel, to
/// start one), and an intent, either the events that record it or a <see cref="DomainException"/>
/// quoting the rule. A flight is live while it runs or is parked, and a parcel has at most one live
/// flight; starting, ending, and stopping therefore also decide the parcel event that records it.
/// </summary>
public static class FlightDecider
{
    public static (FlightStarted Started, ParcelFlightAttached Attached) Start(
        FlowDefinition flow, int flowVersion, Parcel parcel, string startedBy, DateTimeOffset now)
    {
        if (parcel.IsClosed)
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is closed ({parcel.Resolution}); a flight starts only on an open parcel.");
        }

        if (!flow.Accepts.Any(type => DefinitionResolution.SameName(type, parcel.TypeName)))
        {
            throw new DomainException(
                $"Flow '{flow.Name}' accepts parcels of type {string.Join(", ", flow.Accepts)}, not '{parcel.TypeName}'.");
        }

        if (parcel.LiveFlightId is Guid live)
        {
            throw new DomainException(
                $"A parcel has at most one live flight; parcel {Ids.Short(parcel.Id)} already has flight {Ids.Short(live)}, which must end or be stopped first.");
        }

        FlowStep first = flow.First
            ?? throw new DomainException($"Flow '{flow.Name}' has no steps, so a flight has nowhere to start.");
        Guid flightId = Ids.New();
        var started = new FlightStarted(
            flightId, parcel.Id, flow.Name, flowVersion, flow.Hash, startedBy, first.Name, KindOf(first), PerformerOf(first), now);
        return (started, new ParcelFlightAttached(parcel.Id, flightId, now));
    }

    /// <summary>
    /// Starts the next link of a chain: a flight of a flow whose follow matches the ended
    /// <paramref name="parent"/>, on the parent's parcel, recording the parent, the chain's root,
    /// and a depth one greater than the parent's. Every rule of <see cref="Start"/> holds too.
    /// </summary>
    public static (FlightStarted Started, ParcelFlightAttached Attached) Follow(
        FlowDefinition flow, int flowVersion, Parcel parcel, Flight parent, string startedBy, DateTimeOffset now)
    {
        FlowFollow follow = flow.Follow
            ?? throw new DomainException($"Flow '{flow.Name}' declares no follow, so no flight's end starts it.");

        if (parent.Status != FlightStatus.Ended)
        {
            throw new DomainException(
                $"Flight {Ids.Short(parent.Id)} {Describe(parent.Status)}; a follow starts only from a flight that reached its end.");
        }

        if (parent.ParcelId != parcel.Id)
        {
            throw new DomainException(
                $"Flight {Ids.Short(parent.Id)} ran on parcel {Ids.Short(parent.ParcelId)}; a follow runs on the parcel of the flight it follows, not {Ids.Short(parcel.Id)}.");
        }

        bool outcomeMatches = string.Equals(parent.Outcome, follow.Outcome, StringComparison.Ordinal);
        bool flowMatches = follow.Flow is null || DefinitionResolution.SameName(parent.FlowName, follow.Flow);
        if (!outcomeMatches || !flowMatches)
        {
            throw new DomainException(
                $"Flow '{flow.Name}' follows the outcome '{follow.Outcome}'{(follow.Flow is null ? string.Empty : $" of flow '{follow.Flow}'")}; flight {Ids.Short(parent.Id)} of flow '{parent.FlowName}' ended with '{parent.Outcome}'.");
        }

        (FlightStarted started, ParcelFlightAttached attached) = Start(flow, flowVersion, parcel, startedBy, now);
        return (started with { ParentFlightId = parent.Id, RootFlightId = parent.RootFlightId, Depth = parent.Depth + 1 }, attached);
    }

    public static StepStarted StartStep(FlowDefinition flow, Flight flight, string step, DateTimeOffset now)
    {
        RequireFlow(flow, flight);
        RequireRunning(flight, "start a step");

        FlowStep entered = flow.Step(step)
            ?? throw new DomainException($"Flow '{flow.Name}' has no step '{step}'; a flight enters only a step its flow has.");
        return new StepStarted(flight.Id, entered.Name, KindOf(entered), PerformerOf(entered), now);
    }

    public static JudgmentAnswered Answer(
        FlowDefinition flow,
        Flight flight,
        string step,
        string agent,
        string agentHash,
        ModelTriple model,
        JsonElement answer,
        Measurement measurement,
        DateTimeOffset now)
    {
        RequireFlow(flow, flight);
        RequireRunning(flight, "record an answer");
        FlightStepEntry current = RequireCurrent(flight, step, "an answer");

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
            throw new DomainException("An answer records the model triple it ran on: a vendor, a model, and an effort.");
        }

        RequireMeasurement(measurement);

        return new JudgmentAnswered(flight.Id, current.Step, agent, agentHash.Trim(), model, answer.Clone(), measurement, now);
    }

    /// <summary>
    /// Counts one more spawn for a resumed judgment step whose visit parked without an answer, which
    /// is performed again on the same visit and adds no visit. It is counted once per resume, and
    /// never past the flow's spawn cap; a visit's first spawn is counted when the step is entered.
    /// </summary>
    public static JudgmentRespawned Respawn(FlowDefinition flow, Flight flight, string step, DateTimeOffset now)
    {
        RequireFlow(flow, flight);
        RequireRunning(flight, "spawn its judgment again");
        FlightStepEntry current = RequireCurrent(flight, step, "a spawn");

        if (current.Kind != StepKind.Judgment)
        {
            throw new DomainException($"Step '{current.Step}' is an action step; only a judgment step spawns an agent.");
        }

        if (current.ResumedAt is null)
        {
            throw new DomainException(
                $"Step '{current.Step}' has not been resumed since it was entered; its visit's spawn was counted when the flight entered it.");
        }

        if (current.RespawnedAt is not null)
        {
            throw new DomainException(
                $"Step '{current.Step}' already counted a spawn for its latest resume; a resume spawns its judgment again once.");
        }

        if (flight.Spawns >= flow.SpawnCap)
        {
            throw new DomainException(
                $"Flow '{flow.Name}' reached its spawn cap of {flow.SpawnCap}; performing judgment step '{current.Step}' again would spawn another agent.");
        }

        return new JudgmentRespawned(flight.Id, current.Step, current.Performer, now);
    }

    public static ActionCompleted Complete(FlowDefinition flow, Flight flight, string step, string action, JsonElement result, DateTimeOffset now)
    {
        RequireFlow(flow, flight);
        RequireRunning(flight, "record a result");
        FlightStepEntry current = RequireCurrent(flight, step, "a result");

        if (current.Kind != StepKind.Action)
        {
            throw new DomainException($"Step '{current.Step}' is a judgment step; it takes an answer, not a result.");
        }

        if (!string.Equals(current.Performer, action, StringComparison.Ordinal))
        {
            throw new DomainException($"Step '{current.Step}' names the action '{current.Performer}'; a result from '{action}' is not its result.");
        }

        return new ActionCompleted(flight.Id, current.Step, action, result.Clone(), now);
    }

    /// <summary>Parks a running flight; <paramref name="usage"/> is what a judgment that parked without an answer reported.</summary>
    public static FlightParked Park(
        FlowDefinition flow, Flight flight, string reason, string lever, string parkedBy, DateTimeOffset now, Measurement? usage = null)
    {
        RequireFlow(flow, flight);
        RequireRunning(flight, "park it");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("A park records why the flight stopped; give a reason.");
        }

        if (string.IsNullOrWhiteSpace(lever))
        {
            throw new DomainException("A park records the lever a person acts on to resume the flight; give a lever.");
        }

        if (usage is not null)
        {
            RequireMeasurement(usage);
        }

        return new FlightParked(flight.Id, reason.Trim(), lever.Trim(), parkedBy, now, usage);
    }

    public static FlightResumed Resume(FlowDefinition flow, Flight flight, string? note, string resumedBy, DateTimeOffset now)
    {
        RequireFlow(flow, flight);

        if (flight.Status != FlightStatus.Parked)
        {
            throw new DomainException($"Flight {Ids.Short(flight.Id)} {Describe(flight.Status)}; only a parked flight resumes.");
        }

        return new FlightResumed(flight.Id, resumedBy, note?.Trim() ?? string.Empty, now);
    }

    public static (FlightEnded Ended, ParcelFlightReleased Released) End(
        FlowDefinition flow, Flight flight, string outcome, string endedBy, DateTimeOffset now)
    {
        RequireFlow(flow, flight);
        RequireRunning(flight, "end it");

        if (!string.Equals(outcome, flow.Outcome, StringComparison.Ordinal))
        {
            throw new DomainException($"Flow '{flow.Name}' ends with its declared outcome '{flow.Outcome}', not '{outcome}'.");
        }

        return (new FlightEnded(flight.Id, outcome, endedBy, now), new ParcelFlightReleased(flight.ParcelId, flight.Id, now));
    }

    /// <summary>Stops a live flight, running or parked, so that a park nobody resumes does not hold its parcel forever.</summary>
    public static (FlightStopped Stopped, ParcelFlightReleased Released) Stop(Flight flight, string reason, string stoppedBy, DateTimeOffset now)
    {
        if (!flight.IsLive)
        {
            throw new DomainException($"Flight {Ids.Short(flight.Id)} {Describe(flight.Status)}; only a live flight stops.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Stopping a flight records why; give a reason.");
        }

        return (new FlightStopped(flight.Id, stoppedBy, reason.Trim(), now), new ParcelFlightReleased(flight.ParcelId, flight.Id, now));
    }

    private static void RequireMeasurement(Measurement measurement)
    {
        if (measurement.Tokens < 0 || measurement.CostUsd < 0 || measurement.Turns < 0 || measurement.Duration < TimeSpan.Zero)
        {
            throw new DomainException("A measurement is never negative: tokens, cost, turns, and duration are zero or more.");
        }
    }

    private static void RequireFlow(FlowDefinition flow, Flight flight)
    {
        if (!string.Equals(flow.Hash, flight.FlowHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Flight {Ids.Short(flight.Id)} runs '{flight.FlowName}@{flight.FlowHash}', not '{flow.Name}@{flow.Hash}'; a flight keeps the flow it started under.");
        }
    }

    private static void RequireRunning(Flight flight, string intent)
    {
        if (flight.Status != FlightStatus.Running)
        {
            throw new DomainException(
                $"Flight {Ids.Short(flight.Id)} {Describe(flight.Status)}; only a running flight can {intent}.");
        }
    }

    private static FlightStepEntry RequireCurrent(Flight flight, string step, string what)
    {
        FlightStepEntry current = flight.Current
            ?? throw new DomainException($"Flight {Ids.Short(flight.Id)} has entered no step, so {what} has nowhere to go.");

        if (!string.Equals(current.Step, step, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Flight {Ids.Short(flight.Id)} is at step '{current.Step}'; {what} is recorded only for the current step, not '{step}'.");
        }

        if (current.IsRecorded)
        {
            throw new DomainException(
                $"Step '{current.Step}' already holds what this visit recorded; the flight enters the step again before it records another.");
        }

        return current;
    }

    private static string Describe(FlightStatus status) => status switch
    {
        FlightStatus.Running => "is running",
        FlightStatus.Parked => "is parked",
        FlightStatus.Ended => "has ended",
        FlightStatus.Stopped => "has been stopped",
        _ => $"is {status}",
    };

    private static StepKind KindOf(FlowStep step) => step.IsJudgment ? StepKind.Judgment : StepKind.Action;

    private static string PerformerOf(FlowStep step) => (step.IsJudgment ? step.Agent : step.Action) ?? string.Empty;
}
