using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Flows;
using Coanda.Core.Flows.Events;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class FlowChainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Contour Build = ShippedContours.Build;
    private static readonly ModelSpec Model = new("anthropic", "claude-opus-5-5", "high");

    private static readonly Contour Follower = new()
    {
        Name = "after-build",
        Accepts = ["task"],
        Outcome = "followed",
        Budget = 1m,
        SpawnCap = 1,
        Follow = new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 3 },
        Steps =
        [
            new ContourStep
            {
                Name = "look",
                Action = SoftwareActions.Worktree,
                Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("done"), VisitCap = 1, Target = Contour.End }],
            },
        ],
    };

    private static Parcel ParcelOf() =>
        Parcel.Create(new ParcelCreated(Ids.New(), "alpha", "task", "hash", "build", "A parcel", "owner", Now));

    /// <summary>A build flow on the parcel, ended with its outcome, and the parcel as its end leaves it.</summary>
    private static (Flow Flow, Parcel Parcel) EndedBuild()
    {
        Parcel parcel = ParcelOf();
        (FlowStarted started, ParcelFlowAttached attached) = FlowDecider.Start(Build, 1, parcel, "owner", Now);
        parcel.Apply(attached);
        Flow flow = Flow.Create(started);
        (FlowEnded ended, ParcelFlowReleased released) = FlowDecider.End(Build, flow, Build.Outcome, "owner", Now);
        flow.Apply(ended);
        parcel.Apply(released);
        return (flow, parcel);
    }

    [Fact]
    public void A_flow_no_follow_started_is_the_root_of_its_own_chain_at_depth_zero()
    {
        (FlowStarted started, _) = FlowDecider.Start(Build, 1, ParcelOf(), "owner", Now);
        Flow flow = Flow.Create(started);

        started.ParentFlowId.Should().BeNull();
        flow.ParentFlowId.Should().BeNull();
        flow.RootFlowId.Should().Be(flow.Id);
        flow.Depth.Should().Be(0);
    }

    [Fact]
    public void A_follow_records_its_parent_the_chains_root_and_a_depth_one_past_the_parents()
    {
        (Flow parent, Parcel parcel) = EndedBuild();

        (FlowStarted started, ParcelFlowAttached attached) = FlowDecider.Follow(Follower, 1, parcel, parent, "follow:x", Now);
        Flow child = Flow.Create(started);

        child.ParentFlowId.Should().Be(parent.Id);
        child.RootFlowId.Should().Be(parent.Id);
        child.Depth.Should().Be(1);
        child.ParcelId.Should().Be(parcel.Id);
        attached.FlowId.Should().Be(child.Id);

        Contour again = Follower with { Follow = new ContourFollow { Outcome = "followed", DepthCap = 3 } };
        (FlowEnded ended, ParcelFlowReleased released) = FlowDecider.End(Follower, child, Follower.Outcome, "follow:x", Now);
        child.Apply(ended);
        parcel.Apply(attached);
        parcel.Apply(released);
        Flow grandchild = Flow.Create(FlowDecider.Follow(again, 1, parcel, child, "follow:y", Now).Started);
        grandchild.RootFlowId.Should().Be(parent.Id, "the root is carried down the chain");
        grandchild.Depth.Should().Be(2);
    }

    [Fact]
    public void A_follow_from_a_flow_that_has_not_ended_is_refused()
    {
        Parcel parcel = ParcelOf();
        Flow running = Flow.Create(FlowDecider.Start(Build, 1, parcel, "owner", Now).Started);

        FluentActions.Invoking(() => FlowDecider.Follow(Follower, 1, ParcelOf(), running, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("*is running; a follow starts only from a flow that reached its end.");
    }

    [Fact]
    public void A_follow_onto_another_parcel_is_refused()
    {
        (Flow parent, _) = EndedBuild();

        FluentActions.Invoking(() => FlowDecider.Follow(Follower, 1, ParcelOf(), parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("*a follow runs on the parcel of the flow it follows*");
    }

    [Fact]
    public void A_follow_by_a_contour_that_declares_none_or_follows_another_outcome_is_refused()
    {
        (Flow parent, Parcel parcel) = EndedBuild();

        FluentActions.Invoking(() => FlowDecider.Follow(Follower with { Follow = null }, 1, parcel, parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("Contour 'after-build' declares no follow, so no flow's end starts it.");
        FluentActions.Invoking(() => FlowDecider.Follow(
                Follower with { Follow = new ContourFollow { Outcome = "pr-opened", Contour = "discovery", DepthCap = 1 } }, 1, parcel, parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("Contour 'after-build' follows the outcome 'pr-opened' of contour 'discovery'; flow * of contour 'build' ended with 'pr-opened'.");
    }

    [Fact]
    public void The_cost_a_flow_records_sums_its_answers_and_the_usage_of_a_paused_judgment()
    {
        Flow flow = Flow.Create(FlowDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);
        flow.Apply(FlowDecider.StartStep(Build, flow, "build", Now));
        flow.Apply(FlowDecider.Answer(
            Build, flow, "build", "build", "agenthash", Model, Json("""{"verdict":"done"}"""), new Measurement(10, 1.5m, 1, TimeSpan.FromSeconds(1)), Now));
        flow.Apply(FlowDecider.StartStep(Build, flow, "adversarial-review", Now));
        var usage = new Measurement(20, 2.25m, 2, TimeSpan.FromSeconds(2));

        FlowPaused paused = FlowDecider.Pause(Build, flow, "no valid answer", "/transcripts/1.jsonl", "owner", Now, usage);
        flow.Apply(paused);

        paused.Usage.Should().Be(usage);
        flow.PauseUsage.Should().Be(usage);
        flow.Cost.Should().Be(3.75m);

        flow.Apply(FlowDecider.Resume(Build, flow, null, "owner", Now));
        flow.PauseUsage.Should().BeNull("the usage belongs to the pause the resume ends");
        flow.Cost.Should().Be(3.75m, "a resume spends nothing and forgets nothing spent");
    }

    [Fact]
    public void A_follow_names_the_contour_it_follows_in_any_spelling()
    {
        (Flow parent, Parcel parcel) = EndedBuild();
        Contour spelled = Follower with { Follow = new ContourFollow { Outcome = "pr-opened", Contour = "Build", DepthCap = 3 } };

        Flow child = Flow.Create(FlowDecider.Follow(spelled, 1, parcel, parent, "follow:x", Now).Started);

        child.ParentFlowId.Should().Be(parent.Id);
    }

    [Fact]
    public void A_flow_document_from_before_chains_reads_as_its_own_root_and_counts_what_its_answers_cost()
    {
        Flow flow = Flow.Create(FlowDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);
        flow.Apply(FlowDecider.StartStep(Build, flow, "build", Now));
        flow.Apply(FlowDecider.Answer(
            Build, flow, "build", "build", "agenthash", Model, Json("""{"verdict":"done"}"""), new Measurement(10, 1.5m, 1, TimeSpan.FromSeconds(1)), Now));

        JsonObject document = JsonSerializer.SerializeToNode(flow)?.AsObject()
            ?? throw new InvalidOperationException("A flow serialized to nothing.");
        foreach (string added in new[] { nameof(Flow.ParentFlowId), nameof(Flow.RootFlowId), nameof(Flow.Depth), nameof(Flow.PausedCost), nameof(Flow.PauseUsage) })
        {
            document.Remove(added).Should().BeTrue($"the document carries {added} today");
        }

        Flow legacy = document.Deserialize<Flow>() ?? throw new InvalidOperationException("The document read as nothing.");

        legacy.RootFlowId.Should().Be(flow.Id, "a flow recorded before chains has no stored root and is its own");
        legacy.Cost.Should().Be(1.5m, "what its answers cost is read from its recorded visits");
    }

    [Fact]
    public void A_pause_with_a_negative_usage_is_refused()
    {
        Flow flow = Flow.Create(FlowDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);

        FluentActions.Invoking(() => FlowDecider.Pause(Build, flow, "why", "lever", "owner", Now, new Measurement(0, -1m, 0, TimeSpan.Zero)))
            .Should().Throw<DomainException>().WithMessage("A measurement is never negative*");
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
