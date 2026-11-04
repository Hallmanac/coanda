using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flights;
using Coanda.Core.Flights.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class FlightChainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly FlowDefinition Build = ShippedFlows.Build;
    private static readonly ModelSpec Model = new("anthropic", "claude-opus-5-5", "high");

    private static readonly FlowDefinition Follower = new()
    {
        Name = "after-build",
        Accepts = ["task"],
        Outcome = "followed",
        Budget = 1m,
        SpawnCap = 1,
        Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 3 },
        Steps =
        [
            new FlowStep
            {
                Name = "look",
                Action = SoftwareActions.Worktree,
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = FlowDefinition.End }],
            },
        ],
    };

    private static Parcel ParcelOf() =>
        Parcel.Create(new ParcelCreated(Ids.New(), "alpha", "task", "hash", "build", "A parcel", "owner", Now));

    /// <summary>A build flight on the parcel, ended with its outcome, and the parcel as its end leaves it.</summary>
    private static (Flight Flight, Parcel Parcel) EndedBuild()
    {
        Parcel parcel = ParcelOf();
        (FlightStarted started, ParcelFlightAttached attached) = FlightDecider.Start(Build, 1, parcel, "owner", Now);
        parcel.Apply(attached);
        Flight flight = Flight.Create(started);
        (FlightEnded ended, ParcelFlightReleased released) = FlightDecider.End(Build, flight, Build.Outcome, "owner", Now);
        flight.Apply(ended);
        parcel.Apply(released);
        return (flight, parcel);
    }

    [Fact]
    public void A_flight_no_follow_started_is_the_root_of_its_own_chain_at_depth_zero()
    {
        (FlightStarted started, _) = FlightDecider.Start(Build, 1, ParcelOf(), "owner", Now);
        Flight flight = Flight.Create(started);

        started.ParentFlightId.Should().BeNull();
        flight.ParentFlightId.Should().BeNull();
        flight.RootFlightId.Should().Be(flight.Id);
        flight.Depth.Should().Be(0);
    }

    [Fact]
    public void A_follow_records_its_parent_the_chains_root_and_a_depth_one_past_the_parents()
    {
        (Flight parent, Parcel parcel) = EndedBuild();

        (FlightStarted started, ParcelFlightAttached attached) = FlightDecider.Follow(Follower, 1, parcel, parent, "follow:x", Now);
        Flight child = Flight.Create(started);

        child.ParentFlightId.Should().Be(parent.Id);
        child.RootFlightId.Should().Be(parent.Id);
        child.Depth.Should().Be(1);
        child.ParcelId.Should().Be(parcel.Id);
        attached.FlightId.Should().Be(child.Id);

        FlowDefinition again = Follower with { Follow = new FlowFollow { Outcome = "followed", DepthCap = 3 } };
        (FlightEnded ended, ParcelFlightReleased released) = FlightDecider.End(Follower, child, Follower.Outcome, "follow:x", Now);
        child.Apply(ended);
        parcel.Apply(attached);
        parcel.Apply(released);
        Flight grandchild = Flight.Create(FlightDecider.Follow(again, 1, parcel, child, "follow:y", Now).Started);
        grandchild.RootFlightId.Should().Be(parent.Id, "the root is carried down the chain");
        grandchild.Depth.Should().Be(2);
    }

    [Fact]
    public void A_follow_from_a_flight_that_has_not_ended_is_refused()
    {
        Parcel parcel = ParcelOf();
        Flight running = Flight.Create(FlightDecider.Start(Build, 1, parcel, "owner", Now).Started);

        FluentActions.Invoking(() => FlightDecider.Follow(Follower, 1, ParcelOf(), running, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("*is running; a follow starts only from a flight that reached its end.");
    }

    [Fact]
    public void A_follow_onto_another_parcel_is_refused()
    {
        (Flight parent, _) = EndedBuild();

        FluentActions.Invoking(() => FlightDecider.Follow(Follower, 1, ParcelOf(), parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("*a follow runs on the parcel of the flight it follows*");
    }

    [Fact]
    public void A_follow_by_a_flow_that_declares_none_or_follows_another_outcome_is_refused()
    {
        (Flight parent, Parcel parcel) = EndedBuild();

        FluentActions.Invoking(() => FlightDecider.Follow(Follower with { Follow = null }, 1, parcel, parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("Flow 'after-build' declares no follow, so no flight's end starts it.");
        FluentActions.Invoking(() => FlightDecider.Follow(
                Follower with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "discovery", DepthCap = 1 } }, 1, parcel, parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("Flow 'after-build' follows the outcome 'pr-opened' of flow 'discovery'; flight * of flow 'build' ended with 'pr-opened'.");
    }

    [Fact]
    public void The_cost_a_flight_records_sums_its_answers_and_the_usage_of_a_parked_judgment()
    {
        Flight flight = Flight.Create(FlightDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);
        flight.Apply(FlightDecider.StartStep(Build, flight, "build", Now));
        flight.Apply(FlightDecider.Answer(
            Build, flight, "build", "build", "agenthash", Model, Json("""{"verdict":"done"}"""), new Measurement(10, 1.5m, 1, TimeSpan.FromSeconds(1)), Now));
        flight.Apply(FlightDecider.StartStep(Build, flight, "adversarial-review", Now));
        var usage = new Measurement(20, 2.25m, 2, TimeSpan.FromSeconds(2));

        FlightParked parked = FlightDecider.Park(Build, flight, "no valid answer", "/transcripts/1.jsonl", "owner", Now, usage);
        flight.Apply(parked);

        parked.Usage.Should().Be(usage);
        flight.ParkUsage.Should().Be(usage);
        flight.Cost.Should().Be(3.75m);

        flight.Apply(FlightDecider.Resume(Build, flight, null, "owner", Now));
        flight.ParkUsage.Should().BeNull("the usage belongs to the park the resume ends");
        flight.Cost.Should().Be(3.75m, "a resume spends nothing and forgets nothing spent");
    }

    [Fact]
    public void A_follow_names_the_flow_it_follows_in_any_spelling()
    {
        (Flight parent, Parcel parcel) = EndedBuild();
        FlowDefinition spelled = Follower with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "Build", DepthCap = 3 } };

        Flight child = Flight.Create(FlightDecider.Follow(spelled, 1, parcel, parent, "follow:x", Now).Started);

        child.ParentFlightId.Should().Be(parent.Id);
    }

    [Fact]
    public void A_flight_document_from_before_chains_reads_as_its_own_root_and_counts_what_its_answers_cost()
    {
        Flight flight = Flight.Create(FlightDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);
        flight.Apply(FlightDecider.StartStep(Build, flight, "build", Now));
        flight.Apply(FlightDecider.Answer(
            Build, flight, "build", "build", "agenthash", Model, Json("""{"verdict":"done"}"""), new Measurement(10, 1.5m, 1, TimeSpan.FromSeconds(1)), Now));

        JsonObject document = JsonSerializer.SerializeToNode(flight)?.AsObject()
            ?? throw new InvalidOperationException("A flight serialized to nothing.");
        foreach (string added in new[] { nameof(Flight.ParentFlightId), nameof(Flight.RootFlightId), nameof(Flight.Depth), nameof(Flight.ParkedCost), nameof(Flight.ParkUsage) })
        {
            document.Remove(added).Should().BeTrue($"the document carries {added} today");
        }

        Flight legacy = document.Deserialize<Flight>() ?? throw new InvalidOperationException("The document read as nothing.");

        legacy.RootFlightId.Should().Be(flight.Id, "a flight recorded before chains has no stored root and is its own");
        legacy.Cost.Should().Be(1.5m, "what its answers cost is read from its recorded visits");
    }

    [Fact]
    public void A_park_with_a_negative_usage_is_refused()
    {
        Flight flight = Flight.Create(FlightDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);

        FluentActions.Invoking(() => FlightDecider.Park(Build, flight, "why", "lever", "owner", Now, new Measurement(0, -1m, 0, TimeSpan.Zero)))
            .Should().Throw<DomainException>().WithMessage("A measurement is never negative*");
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
