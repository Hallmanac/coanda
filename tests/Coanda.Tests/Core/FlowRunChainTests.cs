using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class FlowRunChainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Flow Build = ShippedFlows.Build;
    private static readonly ModelSpec Model = new("anthropic", "claude-opus-5-5", "high");

    private static readonly Flow Follower = new()
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
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    private static Parcel ParcelOf() =>
        Parcel.Create(new ParcelCreated(Ids.New(), "alpha", "task", "hash", "build", "A parcel", "owner", Now));

    /// <summary>A build flow run on the parcel, ended with its outcome, and the parcel as its end leaves it.</summary>
    private static (FlowRun FlowRun, Parcel Parcel) EndedBuild()
    {
        Parcel parcel = ParcelOf();
        (FlowRunStarted started, ParcelFlowRunAttached attached) = FlowRunDecider.Start(Build, 1, parcel, "owner", Now);
        parcel.Apply(attached);
        FlowRun flowRun = FlowRun.Create(started);
        (FlowRunEnded ended, ParcelFlowRunReleased released) = FlowRunDecider.End(Build, flowRun, Build.Outcome, "owner", Now);
        flowRun.Apply(ended);
        parcel.Apply(released);
        return (flowRun, parcel);
    }

    [Fact]
    public void A_flow_run_no_follow_started_is_the_root_of_its_own_chain_at_depth_zero()
    {
        (FlowRunStarted started, _) = FlowRunDecider.Start(Build, 1, ParcelOf(), "owner", Now);
        FlowRun flowRun = FlowRun.Create(started);

        started.ParentFlowRunId.Should().BeNull();
        flowRun.ParentFlowRunId.Should().BeNull();
        flowRun.RootFlowRunId.Should().Be(flowRun.Id);
        flowRun.Depth.Should().Be(0);
    }

    [Fact]
    public void A_follow_records_its_parent_the_chains_root_and_a_depth_one_past_the_parents()
    {
        (FlowRun parent, Parcel parcel) = EndedBuild();

        (FlowRunStarted started, ParcelFlowRunAttached attached) = FlowRunDecider.Follow(Follower, 1, parcel, parent, "follow:x", Now);
        FlowRun child = FlowRun.Create(started);

        child.ParentFlowRunId.Should().Be(parent.Id);
        child.RootFlowRunId.Should().Be(parent.Id);
        child.Depth.Should().Be(1);
        child.ParcelId.Should().Be(parcel.Id);
        attached.FlowRunId.Should().Be(child.Id);

        Flow again = Follower with { Follow = new FlowFollow { Outcome = "followed", DepthCap = 3 } };
        (FlowRunEnded ended, ParcelFlowRunReleased released) = FlowRunDecider.End(Follower, child, Follower.Outcome, "follow:x", Now);
        child.Apply(ended);
        parcel.Apply(attached);
        parcel.Apply(released);
        FlowRun grandchild = FlowRun.Create(FlowRunDecider.Follow(again, 1, parcel, child, "follow:y", Now).Started);
        grandchild.RootFlowRunId.Should().Be(parent.Id, "the root is carried down the chain");
        grandchild.Depth.Should().Be(2);
    }

    [Fact]
    public void A_follow_from_a_flow_run_that_has_not_ended_is_refused()
    {
        Parcel parcel = ParcelOf();
        FlowRun running = FlowRun.Create(FlowRunDecider.Start(Build, 1, parcel, "owner", Now).Started);

        FluentActions.Invoking(() => FlowRunDecider.Follow(Follower, 1, ParcelOf(), running, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("*is running; a follow starts only from a flow run that reached its end.");
    }

    [Fact]
    public void A_follow_onto_another_parcel_is_refused()
    {
        (FlowRun parent, _) = EndedBuild();

        FluentActions.Invoking(() => FlowRunDecider.Follow(Follower, 1, ParcelOf(), parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("*a follow runs on the parcel of the flow run it follows*");
    }

    [Fact]
    public void A_follow_by_a_flow_that_declares_none_or_follows_another_outcome_is_refused()
    {
        (FlowRun parent, Parcel parcel) = EndedBuild();

        FluentActions.Invoking(() => FlowRunDecider.Follow(Follower with { Follow = null }, 1, parcel, parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("Flow 'after-build' declares no follow, so no flow run's end starts it.");
        FluentActions.Invoking(() => FlowRunDecider.Follow(
                Follower with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "discovery", DepthCap = 1 } }, 1, parcel, parent, "follow:x", Now))
            .Should().Throw<DomainException>().WithMessage("Flow 'after-build' follows the outcome 'pr-opened' of flow 'discovery'; flow run * of flow 'build' ended with 'pr-opened'.");
    }

    [Fact]
    public void The_cost_a_flow_run_records_sums_its_answers_and_the_usage_of_a_paused_judgment()
    {
        FlowRun flowRun = FlowRun.Create(FlowRunDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);
        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "build", Now));
        Guid session = Ids.New();
        flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, "build", "agenthash", Model, Now));
        flowRun.Apply(FlowRunDecider.OpenSession(Build, flowRun, "build", session, Now));
        flowRun.Apply(FlowRunDecider.Answer(
            Build, flowRun, "build", "build", session, "agenthash", Model, Json("""{"verdict":"done"}"""), new Measurement(10, 1.5m, 1, TimeSpan.FromSeconds(1)), Now));
        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "adversarial-review", Now));
        var usage = new Measurement(20, 2.25m, 2, TimeSpan.FromSeconds(2));

        FlowRunPaused paused = FlowRunDecider.Pause(Build, flowRun, "no valid answer", "/transcripts/1.jsonl", "owner", Now, usage);
        flowRun.Apply(paused);

        paused.Usage.Should().Be(usage);
        flowRun.PauseUsage.Should().Be(usage);
        flowRun.Cost.Should().Be(3.75m);

        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, "owner", Now));
        flowRun.PauseUsage.Should().BeNull("the usage belongs to the pause the resume ends");
        flowRun.Cost.Should().Be(3.75m, "a resume spends nothing and forgets nothing spent");
    }

    [Fact]
    public void A_follow_names_the_flow_it_follows_in_any_spelling()
    {
        (FlowRun parent, Parcel parcel) = EndedBuild();
        Flow spelled = Follower with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "Build", DepthCap = 3 } };

        FlowRun child = FlowRun.Create(FlowRunDecider.Follow(spelled, 1, parcel, parent, "follow:x", Now).Started);

        child.ParentFlowRunId.Should().Be(parent.Id);
    }

    [Fact]
    public void A_flow_run_document_from_before_chains_reads_as_its_own_root_and_counts_what_its_answers_cost()
    {
        FlowRun flowRun = FlowRun.Create(FlowRunDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);
        flowRun.Apply(FlowRunDecider.StartStep(Build, flowRun, "build", Now));
        Guid session = Ids.New();
        flowRun.Apply(FlowRunDecider.Spawn(Build, flowRun, "build", "agenthash", Model, Now));
        flowRun.Apply(FlowRunDecider.OpenSession(Build, flowRun, "build", session, Now));
        flowRun.Apply(FlowRunDecider.Answer(
            Build, flowRun, "build", "build", session, "agenthash", Model, Json("""{"verdict":"done"}"""), new Measurement(10, 1.5m, 1, TimeSpan.FromSeconds(1)), Now));

        JsonObject document = JsonSerializer.SerializeToNode(flowRun)?.AsObject()
            ?? throw new InvalidOperationException("A flow run serialized to nothing.");
        foreach (string added in new[] { nameof(FlowRun.ParentFlowRunId), nameof(FlowRun.RootFlowRunId), nameof(FlowRun.Depth), nameof(FlowRun.PausedCost), nameof(FlowRun.PauseUsage) })
        {
            document.Remove(added).Should().BeTrue($"the document carries {added} today");
        }

        FlowRun legacy = document.Deserialize<FlowRun>() ?? throw new InvalidOperationException("The document read as nothing.");

        legacy.RootFlowRunId.Should().Be(flowRun.Id, "a flow run recorded before chains has no stored root and is its own");
        legacy.Cost.Should().Be(1.5m, "what its answers cost is read from its recorded visits");
    }

    [Fact]
    public void A_pause_with_a_negative_usage_is_refused()
    {
        FlowRun flowRun = FlowRun.Create(FlowRunDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);

        FluentActions.Invoking(() => FlowRunDecider.Pause(Build, flowRun, "why", "lever", "owner", Now, new Measurement(0, -1m, 0, TimeSpan.Zero)))
            .Should().Throw<DomainException>().WithMessage("A measurement is never negative*");
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
