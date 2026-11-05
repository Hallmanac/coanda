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
using Coanda.Modules.Software;

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
                SystemStep = SoftwareSteps.Worktree,
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

        Assert.Null(started.ParentFlowRunId);
        Assert.Null(flowRun.ParentFlowRunId);
        Assert.Equal(flowRun.Id, flowRun.RootFlowRunId);
        Assert.Equal(0, flowRun.Depth);
    }

    [Fact]
    public void A_follow_records_its_parent_the_chains_root_and_a_depth_one_past_the_parents()
    {
        (FlowRun parent, Parcel parcel) = EndedBuild();

        (FlowRunStarted started, ParcelFlowRunAttached attached) = FlowRunDecider.Follow(Follower, 1, parcel, parent, "follow:x", Now);
        FlowRun child = FlowRun.Create(started);

        Assert.Equal(parent.Id, child.ParentFlowRunId);
        Assert.Equal(parent.Id, child.RootFlowRunId);
        Assert.Equal(1, child.Depth);
        Assert.Equal(parcel.Id, child.ParcelId);
        Assert.Equal(child.Id, attached.FlowRunId);

        Flow again = Follower with { Follow = new FlowFollow { Outcome = "followed", DepthCap = 3 } };
        (FlowRunEnded ended, ParcelFlowRunReleased released) = FlowRunDecider.End(Follower, child, Follower.Outcome, "follow:x", Now);
        child.Apply(ended);
        parcel.Apply(attached);
        parcel.Apply(released);
        FlowRun grandchild = FlowRun.Create(FlowRunDecider.Follow(again, 1, parcel, child, "follow:y", Now).Started);
        // the root is carried down the chain
        Assert.Equal(parent.Id, grandchild.RootFlowRunId);
        Assert.Equal(2, grandchild.Depth);
    }

    [Fact]
    public void A_follow_from_a_flow_run_that_has_not_ended_is_refused()
    {
        Parcel parcel = ParcelOf();
        FlowRun running = FlowRun.Create(FlowRunDecider.Start(Build, 1, parcel, "owner", Now).Started);

        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Follow(Follower, 1, ParcelOf(), running, "follow:x", Now));
        Assert.EndsWith("is running; a follow starts only from a flow run that reached its end.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_follow_onto_another_parcel_is_refused()
    {
        (FlowRun parent, _) = EndedBuild();

        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Follow(Follower, 1, ParcelOf(), parent, "follow:x", Now));
        Assert.Contains("a follow runs on the parcel of the flow run it follows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_follow_by_a_flow_that_declares_none_or_follows_another_outcome_is_refused()
    {
        (FlowRun parent, Parcel parcel) = EndedBuild();

        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Follow(Follower with { Follow = null }, 1, parcel, parent, "follow:x", Now));
        Assert.Equal("Flow 'after-build' declares no follow, so no flow run's end starts it.", exception.Message);
        DomainException exception2 = Assert.Throws<DomainException>(() => FlowRunDecider.Follow(
                Follower with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "discovery", DepthCap = 1 } }, 1, parcel, parent, "follow:x", Now));
        Assert.Matches(@"(?s)^Flow 'after-build' follows the outcome 'pr-opened' of flow 'discovery'; flow run .* of flow 'build' ended with 'pr-opened'\.$", exception2.Message);
    }

    [Fact]
    public void The_cost_a_flow_run_records_sums_its_answers_and_the_usage_of_a_paused_agent_step()
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

        Assert.Equal(usage, paused.Usage);
        Assert.Equal(usage, flowRun.PauseUsage);
        Assert.Equal(3.75m, flowRun.Cost);

        flowRun.Apply(FlowRunDecider.Resume(Build, flowRun, null, "owner", Now));
        // the usage belongs to the pause the resume ends
        Assert.Null(flowRun.PauseUsage);
        // a resume spends nothing and forgets nothing spent
        Assert.Equal(3.75m, flowRun.Cost);
    }

    [Fact]
    public void A_follow_names_the_flow_it_follows_in_any_spelling()
    {
        (FlowRun parent, Parcel parcel) = EndedBuild();
        Flow spelled = Follower with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "Build", DepthCap = 3 } };

        FlowRun child = FlowRun.Create(FlowRunDecider.Follow(spelled, 1, parcel, parent, "follow:x", Now).Started);

        Assert.Equal(parent.Id, child.ParentFlowRunId);
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
            Assert.True(document.Remove(added), $"the document carries {added} today");
        }

        FlowRun legacy = document.Deserialize<FlowRun>() ?? throw new InvalidOperationException("The document read as nothing.");

        // a flow run recorded before chains has no stored root and is its own
        Assert.Equal(flowRun.Id, legacy.RootFlowRunId);
        // what its answers cost is read from its recorded visits
        Assert.Equal(1.5m, legacy.Cost);
    }

    [Fact]
    public void A_pause_with_a_negative_usage_is_refused()
    {
        FlowRun flowRun = FlowRun.Create(FlowRunDecider.Start(Build, 1, ParcelOf(), "owner", Now).Started);

        DomainException exception = Assert.Throws<DomainException>(() => FlowRunDecider.Pause(Build, flowRun, "why", "lever", "owner", Now, new Measurement(0, -1m, 0, TimeSpan.Zero)));
        Assert.StartsWith("A measurement is never negative", exception.Message, StringComparison.Ordinal);
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
