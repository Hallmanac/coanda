using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flights;
using Coanda.Core.Flights.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class FlightDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly FlowDefinition Build = ShippedFlows.Build;
    private static readonly ModelTriple Model = new("anthropic", "claude-opus-5-5", "high");
    private static readonly Measurement Spend = new(1200, 0.42m, 3, TimeSpan.FromSeconds(63));

    private static Parcel ParcelOf(string type = "task") =>
        Parcel.Create(new ParcelCreated(Ids.New(), "alpha", type, "hash", "build", "A parcel", "owner", Now));

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A well-formed answer from the build agent for <paramref name="step"/>.</summary>
    private static JudgmentAnswered AnswerAt(Flight flight, string step) =>
        FlightDecider.Answer(Build, flight, step, "build", "agenthash", Model, Json("""{"verdict":"done"}"""), Spend, Now);

    /// <summary>A build flight started on a fresh parcel, standing at its first step, the worktree action.</summary>
    private static Flight Started(FlowDefinition? flow = null)
    {
        (FlightStarted started, _) = FlightDecider.Start(flow ?? Build, 1, ParcelOf(), "owner", Now);
        return Flight.Create(started);
    }

    /// <summary>A build flight at its judgment step, 'build'.</summary>
    private static Flight AtJudgment()
    {
        Flight flight = Started();
        flight.Apply(FlightDecider.StartStep(Build, flight, "build", Now));
        return flight;
    }

    private static Flight InStatus(FlightStatus status)
    {
        Flight flight = Started();
        switch (status)
        {
            case FlightStatus.Parked:
                flight.Apply(FlightDecider.Park(Build, flight, "waiting", "answer the question", "owner", Now));
                break;
            case FlightStatus.Ended:
                flight.Apply(FlightDecider.End(Build, flight, Build.Outcome, "owner", Now).Ended);
                break;
            case FlightStatus.Stopped:
                flight.Apply(FlightDecider.Stop(flight, "no longer wanted", "owner", Now).Stopped);
                break;
        }

        return flight;
    }

    [Fact]
    public void Replaying_a_start_and_two_step_starts_yields_the_interpreters_position()
    {
        Flight flight = Started();
        flight.Position.Should().Be(Interpreter.Start(Build), "a started flight has already visited its first step once");

        flight.Apply(FlightDecider.StartStep(Build, flight, "build", Now));
        flight.Apply(FlightDecider.StartStep(Build, flight, "gates", Now));

        FlightPosition expected = Interpreter.Apply(
            Build, Interpreter.Apply(Build, Interpreter.Start(Build), new FlightMove.GoTo("build")), new FlightMove.GoTo("gates"));
        flight.Position.Should().Be(expected);
        flight.Position.Step.Should().Be("gates");
        flight.Position.Spawns.Should().Be(1, "only the judgment step 'build' spawned an agent");
        flight.Position.VisitsTo("worktree").Should().Be(1);
        flight.Position.VisitsTo("build").Should().Be(1);
        flight.Position.VisitsTo("gates").Should().Be(1);
    }

    [Fact]
    public void A_flight_whose_first_step_is_a_judgment_counts_one_spawn_at_start()
    {
        Flight flight = Started(ShippedFlows.ReviewOnAssignment);

        flight.Position.Should().Be(Interpreter.Start(ShippedFlows.ReviewOnAssignment));
        flight.Position.Spawns.Should().Be(1);
    }

    [Fact]
    public void Replay_reads_the_events_alone_and_never_the_flow()
    {
        Flight flight = Flight.Create(new FlightStarted(
            Ids.New(), Ids.New(), "any", 1, "h", "owner", "ask", StepKind.Judgment, "an-agent", Now));
        flight.Apply(new StepStarted(flight.Id, "do", StepKind.Action, "an-action", Now));
        flight.Apply(new StepStarted(flight.Id, "ask", StepKind.Judgment, "an-agent", Now));

        flight.Position.Step.Should().Be("ask");
        flight.Position.VisitsTo("ask").Should().Be(2);
        flight.Position.Spawns.Should().Be(2);
    }

    [Fact]
    public void A_start_attaches_the_flight_to_its_parcel_and_records_the_flow_and_first_step()
    {
        Parcel parcel = ParcelOf();
        (FlightStarted started, ParcelFlightAttached attached) = FlightDecider.Start(Build, 3, parcel, "owner", Now);

        started.ParcelId.Should().Be(parcel.Id);
        started.FlowName.Should().Be("build");
        started.FlowVersion.Should().Be(3);
        started.FlowHash.Should().Be(Build.Hash);
        started.FirstStep.Should().Be("worktree");
        started.FirstStepKind.Should().Be(StepKind.Action);
        started.FirstStepPerformer.Should().Be(SoftwareActions.Worktree);
        attached.FlightId.Should().Be(started.FlightId);
        attached.ParcelId.Should().Be(parcel.Id);
    }

    [Fact]
    public void A_start_on_a_closed_parcel_is_refused()
    {
        Parcel parcel = ParcelOf();
        parcel.Apply(new ParcelClosed(parcel.Id, "build", "closed", "abandoned", "Not needed.", "owner", Now));

        Action start = () => FlightDecider.Start(Build, 1, parcel, "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("*is closed (abandoned); a flight starts only on an open parcel*");
    }

    [Fact]
    public void A_start_on_a_parcel_whose_type_the_flow_does_not_accept_is_refused()
    {
        Action start = () => FlightDecider.Start(Build, 1, ParcelOf("idea"), "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("Flow 'build' accepts parcels of type task, not 'idea'.");
    }

    [Fact]
    public void A_start_on_a_parcel_that_already_has_a_live_flight_is_refused()
    {
        Parcel parcel = ParcelOf();
        parcel.Apply(new ParcelFlightAttached(parcel.Id, Ids.New(), Now));

        Action start = () => FlightDecider.Start(Build, 1, parcel, "owner", Now);

        start.Should().Throw<DomainException>().WithMessage("A parcel has at most one live flight;*");
    }

    public static TheoryData<FlightStatus, string> NotRunning()
    {
        var data = new TheoryData<FlightStatus, string>();
        foreach (FlightStatus status in new[] { FlightStatus.Parked, FlightStatus.Ended, FlightStatus.Stopped })
        {
            foreach (string intent in new[] { "step", "answer", "result", "park", "end" })
            {
                data.Add(status, intent);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NotRunning))]
    public void Recording_on_a_flight_that_is_not_running_is_refused(FlightStatus status, string intent)
    {
        Flight flight = InStatus(status);

        Action record = intent switch
        {
            "step" => () => FlightDecider.StartStep(Build, flight, "build", Now),
            "answer" => () => FlightDecider.Answer(Build, flight, "worktree", "build", "agenthash", Model, Json("{}"), Spend, Now),
            "result" => () => FlightDecider.Complete(Build, flight, "worktree", SoftwareActions.Worktree, Json("{}"), Now),
            "park" => () => FlightDecider.Park(Build, flight, "why", "lever", "owner", Now),
            "end" => () => FlightDecider.End(Build, flight, Build.Outcome, "owner", Now),
            _ => throw new ArgumentOutOfRangeException(nameof(intent)),
        };

        record.Should().Throw<DomainException>().WithMessage("*; only a running flight can *");
    }

    [Fact]
    public void Starting_a_step_the_flow_does_not_have_is_refused()
    {
        Action step = () => FlightDecider.StartStep(Build, Started(), "deploy", Now);

        step.Should().Throw<DomainException>().WithMessage("Flow 'build' has no step 'deploy'; a flight enters only a step its flow has.");
    }

    [Fact]
    public void An_answer_for_a_step_other_than_the_current_one_is_refused()
    {
        Action answer = () => AnswerAt(AtJudgment(), "fix");

        answer.Should().Throw<DomainException>().WithMessage("*is at step 'build'; an answer is recorded only for the current step, not 'fix'.");
    }

    [Fact]
    public void A_result_for_a_step_other_than_the_current_one_is_refused()
    {
        Action result = () => FlightDecider.Complete(Build, Started(), "gates", SoftwareActions.Gates, Json("{}"), Now);

        result.Should().Throw<DomainException>().WithMessage("*is at step 'worktree'; a result is recorded only for the current step, not 'gates'.");
    }

    [Fact]
    public void An_answer_on_an_action_step_is_refused()
    {
        Action answer = () => FlightDecider.Answer(Build, Started(), "worktree", "build", "agenthash", Model, Json("{}"), Spend, Now);

        answer.Should().Throw<DomainException>().WithMessage("Step 'worktree' is an action step; it takes a result, not an answer.");
    }

    [Fact]
    public void A_result_on_a_judgment_step_is_refused()
    {
        Action result = () => FlightDecider.Complete(Build, AtJudgment(), "build", "build", Json("{}"), Now);

        result.Should().Throw<DomainException>().WithMessage("Step 'build' is a judgment step; it takes an answer, not a result.");
    }

    [Fact]
    public void An_answer_records_the_agent_its_hash_the_model_the_document_and_the_measurement()
    {
        Flight flight = AtJudgment();
        JudgmentAnswered answered = AnswerAt(flight, "build");
        flight.Apply(answered);

        answered.Agent.Should().Be("build");
        answered.AgentHash.Should().Be("agenthash");
        answered.Model.Should().Be(Model);
        answered.Answer.GetProperty("verdict").GetString().Should().Be("done");
        answered.Measurement.Should().Be(Spend);
        FlightStepEntry? current = flight.Current;
        current.Should().NotBeNull();
        current.Answer.HasValue.Should().BeTrue();
        current.Answer.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("done");
        current.Measurement.Should().Be(Spend);
    }

    [Fact]
    public void A_second_answer_on_the_same_visit_is_refused()
    {
        Flight flight = AtJudgment();
        flight.Apply(AnswerAt(flight, "build"));

        Action again = () => AnswerAt(flight, "build");

        again.Should().Throw<DomainException>().WithMessage("Step 'build' already holds what this visit recorded*");
    }

    [Fact]
    public void An_answer_from_an_agent_the_step_does_not_name_is_refused()
    {
        Action answer = () => FlightDecider.Answer(Build, AtJudgment(), "build", "fix", "agenthash", Model, Json("{}"), Spend, Now);

        answer.Should().Throw<DomainException>().WithMessage("Step 'build' names the agent 'build'; an answer from 'fix' is not its answer.");
    }

    [Fact]
    public void A_negative_measurement_is_refused()
    {
        Action answer = () => FlightDecider.Answer(
            Build, AtJudgment(), "build", "build", "agenthash", Model, Json("{}"), Spend with { CostUsd = -1m }, Now);

        answer.Should().Throw<DomainException>().WithMessage("A measurement is never negative*");
    }

    [Theory]
    [InlineData("", "answer the question", "A park records why the flight stopped; give a reason.")]
    [InlineData("waiting", " ", "A park records the lever a person acts on to resume the flight; give a lever.")]
    public void A_park_with_an_empty_reason_or_lever_is_refused(string reason, string lever, string message)
    {
        Action park = () => FlightDecider.Park(Build, Started(), reason, lever, "owner", Now);

        park.Should().Throw<DomainException>().WithMessage(message);
    }

    [Fact]
    public void An_end_with_an_outcome_other_than_the_flows_is_refused()
    {
        Action end = () => FlightDecider.End(Build, Started(), "merged", "owner", Now);

        end.Should().Throw<DomainException>().WithMessage("Flow 'build' ends with its declared outcome 'pr-opened', not 'merged'.");
    }

    [Theory]
    [InlineData(FlightStatus.Running)]
    [InlineData(FlightStatus.Ended)]
    [InlineData(FlightStatus.Stopped)]
    public void Resuming_a_flight_that_is_not_parked_is_refused(FlightStatus status)
    {
        Action resume = () => FlightDecider.Resume(Build, InStatus(status), "go on", "owner", Now);

        resume.Should().Throw<DomainException>().WithMessage("*; only a parked flight resumes.");
    }

    [Fact]
    public void A_parked_flight_resumes_and_runs_again()
    {
        Flight flight = InStatus(FlightStatus.Parked);
        flight.IsLive.Should().BeTrue("a parked flight is still live");

        flight.Apply(FlightDecider.Resume(Build, flight, "answered", "owner", Now));

        flight.Status.Should().Be(FlightStatus.Running);
        flight.Lever.Should().BeNull();
    }

    [Theory]
    [InlineData(FlightStatus.Ended, "has ended")]
    [InlineData(FlightStatus.Stopped, "has been stopped")]
    public void Stopping_a_flight_that_has_ended_or_stopped_is_refused(FlightStatus status, string described)
    {
        Action stop = () => FlightDecider.Stop(InStatus(status), "enough", "owner", Now);

        stop.Should().Throw<DomainException>().WithMessage($"*{described}; only a live flight stops.");
    }

    [Theory]
    [InlineData(FlightStatus.Running)]
    [InlineData(FlightStatus.Parked)]
    public void A_live_flight_stops_and_releases_its_parcel(FlightStatus status)
    {
        Flight flight = InStatus(status);

        (FlightStopped stopped, ParcelFlightReleased released) = FlightDecider.Stop(flight, "enough", "owner", Now);
        flight.Apply(stopped);

        flight.IsLive.Should().BeFalse();
        released.ParcelId.Should().Be(flight.ParcelId);
        released.FlightId.Should().Be(flight.Id);
    }

    [Fact]
    public void Recording_against_a_flow_other_than_the_one_the_flight_started_under_is_refused()
    {
        Action step = () => FlightDecider.StartStep(ShippedFlows.Discovery, Started(), "discover", Now);

        step.Should().Throw<DomainException>().WithMessage("*a flight keeps the flow it started under.");
    }
}
