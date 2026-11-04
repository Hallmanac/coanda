using System.Collections.Immutable;
using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class ShippedFlowsTests
{
    /// <summary>
    /// Routes the way the interpreter will, with equality only: the first transition of the step
    /// whose field equals the value. Test scaffolding only; the interpreter is a later task.
    /// </summary>
    private static string Next(Flow flow, string step, string verdict)
    {
        FlowStep from = flow.Step(step) ?? throw new InvalidOperationException($"No step '{step}'.");
        return from.Transitions
            .Single(t => t.Field == "verdict" && t.Value.GetString() == verdict)
            .Target;
    }

    private static List<string> Walk(Flow flow, Func<FlowStep, string> answer)
    {
        List<string> visited = [];
        string at = flow.First!.Name;
        while (at != Flow.End)
        {
            visited.Add(at);
            FlowStep step = flow.Step(at)!;
            at = Next(flow, at, answer(step));
        }

        return visited;
    }

    [Fact]
    public void The_four_shipped_flows_pass_validation_with_the_known_system_step_names()
    {
        Assert.Equal(["build", "merge", "discovery", "review-on-assignment"], ShippedFlows.All.Select(f => f.Name));
        foreach (Flow flow in ShippedFlows.All)
        {
            flow.Validate(ShippedSystemSteps.Names);
        }
    }

    [Fact]
    public void The_shipped_system_steps_are_the_core_catalog_and_the_software_catalog()
    {
        string[] expected =
        [
            "wait-for-human", "wait-for-external-event", "timer", "emit-outcome", "move-parcel", "derive-parcels",
            "worktree", "gates", "open-pr", "gated-merge", "rebase",
        ];
        Assert.Equivalent(expected, ShippedSystemSteps.Names, strict: true);
    }

    [Fact]
    public void The_shipped_flows_accept_types_that_ship_and_declare_their_outcomes()
    {
        (string Name, string Accepts, string Outcome)[] expected =
        [
            ("build", "task", "pr-opened"),
            ("merge", "task", "merged"),
            ("discovery", "idea", "parcels-derived"),
            ("review-on-assignment", "task", "report-produced"),
        ];

        foreach ((string name, string accepts, string outcome) in expected)
        {
            Flow flow = ShippedFlows.Find(name)!;
            Assert.Equal([accepts], flow.Accepts);
            Assert.Equal(outcome, flow.Outcome);
            Assert.NotNull(ShippedTypes.Find(accepts));
        }
    }

    [Fact]
    public void No_shipped_flow_holds_an_emit_outcome_step()
    {
        Assert.DoesNotContain(ShippedFlows.All.SelectMany(f => f.Steps), s => s.SystemStep == CoreSystemSteps.EmitOutcome);
    }

    [Fact]
    public void The_build_flow_on_done_and_clean_visits_the_steps_in_order_and_reaches_end()
    {
        Flow build = ShippedFlows.Build;

        List<string> visited = Walk(build, step => step.Name switch
        {
            "adversarial-review" or "conformance-review" => "clean",
            "open-pr" => "opened",
            _ => "done",
        });

        List<string> systemSteps = [.. visited.Select(name => build.Step(name)!.SystemStep ?? build.Step(name)!.Agent!)];
        Assert.Equal(["worktree", "build", "gates", "reviewer-adversarial", "reviewer-conformance", "open-pr", "move-parcel"], systemSteps);
        // the merge is a flow of its own that follows pr-opened
        Assert.DoesNotContain(build.Steps, s => s.SystemStep == SoftwareSystemSteps.GatedMerge);
        Assert.Equal("worktree", build.First!.Name);
        Assert.Equal(1, build.Steps.Count(s => s.SystemStep == CoreSystemSteps.MoveParcel));

        FlowStep move = build.Step(visited[^1])!;
        Assert.Equal("stage", Assert.Single(move.Arguments).Key);
        Assert.Equal("review", move.Arguments["stage"].GetString());
        Assert.True(ShippedTypes.Task.AllowsMove("build", "review"));
    }

    [Fact]
    public void In_the_build_flow_not_clean_from_either_review_and_failed_from_gates_lead_to_fix_and_fix_done_returns_to_gates()
    {
        Flow build = ShippedFlows.Build;

        Assert.Equal("fix", Next(build, "adversarial-review", "not-clean"));
        Assert.Equal("fix", Next(build, "conformance-review", "not-clean"));
        Assert.Equal("fix", Next(build, "gates", "failed"));
        Assert.Equal("gates", Next(build, "fix", "done"));
        Assert.Equal("conformance-review", Next(build, "adversarial-review", "clean"));
    }

    [Fact]
    public void In_the_build_flow_blocked_answers_pause()
    {
        Assert.Equal(Flow.Pause, Next(ShippedFlows.Build, "build", "blocked"));
        Assert.Equal(Flow.Pause, Next(ShippedFlows.Build, "fix", "blocked"));
    }

    [Fact]
    public void Review_to_fix_transitions_cap_at_three_and_every_other_build_transition_at_five()
    {
        Flow build = ShippedFlows.Build;
        var reviews = new HashSet<string> { "adversarial-review", "conformance-review" };

        foreach (FlowStep step in build.Steps)
        {
            foreach (FlowTransition transition in step.Transitions)
            {
                int expected = reviews.Contains(step.Name) && transition.Target == "fix" ? 3 : 5;
                // {step.Name} on {transition.Value} goes to {transition.Target}
                Assert.Equal(expected, transition.VisitCap);
            }
        }
    }

    [Fact]
    public void The_build_flow_names_the_agents_and_schemas_the_agent_definitions_seed()
    {
        Assert.Equal([("build", "build", "build"), ("adversarial-review", "reviewer-adversarial", "review"), ("conformance-review", "reviewer-conformance", "review"), ("fix", "fix", "fix")], ShippedFlows.Build.Steps.Where(s => s.IsAgentStep).Select(s => (s.Name, s.Agent, s.AnswerSchema)));
    }

    [Fact]
    public void The_discovery_flow_has_one_agent_step_that_derives_or_carries_forward()
    {
        Flow discovery = ShippedFlows.Discovery;

        Assert.Equal(1, discovery.Steps.Count(s => s.IsAgentStep));
        FlowStep agentStep = discovery.First!;
        Assert.Equal("discovery", agentStep.Agent);
        Assert.Equal("discovery", agentStep.AnswerSchema);

        string derive = Next(discovery, agentStep.Name, "derive");
        Assert.Equal(CoreSystemSteps.DeriveParcels, discovery.Step(derive)!.SystemStep);
        Assert.Equal(Flow.End, Next(discovery, derive, "done"));
        Assert.Equal(Flow.End, Next(discovery, agentStep.Name, "carry-forward"));
    }

    [Fact]
    public void The_merge_flow_follows_the_builds_pr_opened_and_closes_its_task_as_completed()
    {
        Flow merge = ShippedFlows.Merge;

        Assert.Equal(new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = merge.Follow!.DepthCap }, merge.Follow);
        Assert.True(merge.Follow.DepthCap > 0, $"Expected a positive depth cap, but found {merge.Follow.DepthCap}.");
        Assert.Equal("completed", merge.Closes);
        Assert.True(ShippedTypes.Task.IsResolution(merge.Closes!));
        Assert.All(merge.Steps.SelectMany(s => s.Transitions), t => Assert.True(t.VisitCap > 0));
        Assert.Equal(SoftwareSystemSteps.GatedMerge, merge.First!.SystemStep);
        Assert.Equal(Flow.End, Next(merge, "gated-merge", "merged"));
    }

    [Fact]
    public void The_merge_flows_lap_waits_at_least_twenty_minutes_with_its_rebase_laps_taken_out_before_it_pauses_and_a_late_merge_still_ends()
    {
        Flow merge = ShippedFlows.Merge;
        FlowStep gated = merge.Step("gated-merge")!;
        FlowTransition pending = gated.Transitions.Single(t => t.Value.GetString() == "pending");
        FlowTransition merged = gated.Transitions.Single(t => t.Value.GetString() == "merged");
        FlowStep timer = merge.Step(pending.Target)!;
        FlowTransition back = Assert.Single(timer.Transitions);
        FlowTransition rebaseLaps = RebaseLapCap(merge);

        Assert.Equal(CoreSystemSteps.Timer, timer.SystemStep);
        Assert.Equal("gated-merge", back.Target);
        double seconds = timer.Arguments["seconds"].GetDouble();
        double waited = seconds * (pending.VisitCap - rebaseLaps.VisitCap);
        Assert.True(waited >= 1200, $"Expected at least 1200 seconds, but found {waited}: the lap waits at least twenty minutes in all once the rebase laps' visits are taken out.");
        Assert.True(back.VisitCap >= pending.VisitCap, $"Expected at least {pending.VisitCap}, but found {back.VisitCap}: the timer never pauses the lap before the pending cap does.");
        Assert.True(merged.VisitCap >= pending.VisitCap + 100, $"Expected at least {pending.VisitCap + 100}, but found {merged.VisitCap}: a merge on the lap's last visit or on any of a hundred resumes still ends the flow run.");
        Assert.True(merged.VisitCap >= pending.VisitCap + 5, $"Expected at least {pending.VisitCap + 5}, but found {merged.VisitCap}.");
    }

    [Fact]
    public void The_merge_flow_routes_a_behind_head_through_a_worktree_and_a_rebase_back_to_the_timer_with_no_agent_step()
    {
        Flow merge = ShippedFlows.Merge;
        FlowStep gated = merge.Step("gated-merge")!;
        FlowTransition pending = gated.Transitions.Single(t => t.Value.GetString() == "pending");
        FlowTransition behind = gated.Transitions.Single(t => t.Value.GetString() == "behind");
        FlowStep worktree = merge.Step(behind.Target)!;
        FlowTransition checkedOut = Assert.Single(worktree.Transitions);
        FlowStep rebase = merge.Step(checkedOut.Target)!;
        FlowTransition rebased = Assert.Single(rebase.Transitions);

        Assert.Equal(SoftwareSystemSteps.Worktree, worktree.SystemStep);
        Assert.Equal("done", checkedOut.Value.GetString());
        Assert.Equal(SoftwareSystemSteps.Rebase, rebase.SystemStep);
        Assert.Equal("done", rebased.Value.GetString());
        // the rebase waits one timer period before the pull request is inspected again
        Assert.Equal(pending.Target, rebased.Target);
        // the lap runs no agent step and re-runs no review
        Assert.DoesNotContain(merge.Steps, s => s.IsAgentStep);
        // a head is rebased on any visit the pending cap still allows
        Assert.Equal(pending.VisitCap, behind.VisitCap);
        Assert.True(checkedOut.VisitCap >= 1, $"Expected at least 1, but found {checkedOut.VisitCap}.");
        Assert.True(checkedOut.VisitCap < pending.VisitCap, $"Expected less than {pending.VisitCap}, but found {checkedOut.VisitCap}.");
        Assert.True(rebased.VisitCap >= checkedOut.VisitCap, $"Expected at least {checkedOut.VisitCap}, but found {rebased.VisitCap}.");
    }

    [Fact]
    public void The_merge_flows_rebase_laps_pause_on_the_worktree_steps_cap_once_spent_before_the_rebase()
    {
        Flow merge = ShippedFlows.Merge;
        int laps = RebaseLapCap(merge).VisitCap;
        FlowRunPosition position = Interpreter.Start(merge);
        using JsonDocument behind = JsonDocument.Parse("""{"verdict":"behind"}""");
        using JsonDocument done = JsonDocument.Parse("""{"verdict":"done"}""");

        for (int lap = 1; lap <= laps; lap++)
        {
            position = Go(position, behind, "worktree");
            position = Go(position, done, "rebase");
            position = Go(position, done, "wait-for-checks");
            position = Go(position, done, "gated-merge");
        }

        position = Go(position, behind, "worktree");
        Assert.Equal($"Step 'worktree' reached the visit cap of {laps} on its transition to 'rebase'.", Assert.IsType<FlowRunMove.Pause>(Interpreter.Decide(merge, position, done)).Reason);

        FlowRunPosition Go(FlowRunPosition from, JsonDocument answer, string step)
        {
            FlowRunMove move = Interpreter.Decide(merge, from, answer);
            Assert.Equal(new FlowRunMove.GoTo(step), move);
            return Interpreter.Apply(merge, from, (FlowRunMove.GoTo)move);
        }
    }

    [Fact]
    public void The_build_discovery_and_review_on_assignment_flows_hash_to_literals()
    {
        Assert.Equal("dcbfd639e88354c7", ShippedFlows.Build.Hash);
        Assert.Equal("c8b01e99a6cc7916", ShippedFlows.Discovery.Hash);
        Assert.Equal("fabaabb2eb82ff10", ShippedFlows.ReviewOnAssignment.Hash);
    }

    /// <summary>The visit cap on the merge's worktree step's transition to the rebase, which bounds the rebase laps.</summary>
    private static FlowTransition RebaseLapCap(Flow merge)
    {
        FlowTransition behind = merge.Step("gated-merge")!.Transitions.Single(t => t.Value.GetString() == "behind");
        return Assert.Single(merge.Step(behind.Target)!.Transitions);
    }

    [Fact]
    public void The_merge_flows_pending_cap_pauses_the_flow_run_once_spent_rather_than_waiting_again()
    {
        Flow merge = ShippedFlows.Merge;
        int cap = ShippedFlows.MergePendingCap;
        FlowRunPosition position = Interpreter.Start(merge);
        using JsonDocument pending = JsonDocument.Parse("""{"verdict":"pending"}""");
        using JsonDocument done = JsonDocument.Parse("""{"verdict":"done"}""");

        for (int lap = 1; lap <= cap; lap++)
        {
            FlowRunMove wait = Interpreter.Decide(merge, position, pending);
            // lap {lap} is within the pending cap
            Assert.Equal(new FlowRunMove.GoTo("wait-for-checks"), wait);
            position = Interpreter.Apply(merge, position, (FlowRunMove.GoTo)wait);
            FlowRunMove again = Interpreter.Decide(merge, position, done);
            Assert.Equal(new FlowRunMove.GoTo("gated-merge"), again);
            position = Interpreter.Apply(merge, position, (FlowRunMove.GoTo)again);
        }

        Assert.Contains($"visit cap of {cap}", Assert.IsType<FlowRunMove.Pause>(Interpreter.Decide(merge, position, pending)).Reason, StringComparison.Ordinal);
        using JsonDocument merged = JsonDocument.Parse("""{"verdict":"merged"}""");
        // a merge on the lap's last visit ends the flow run
        Assert.Equal(new FlowRunMove.End("merged"), Interpreter.Decide(merge, position, merged));
    }

    [Fact]
    public void A_merge_on_the_hundredth_resume_after_the_lap_pauses_still_ends_the_flow_run()
    {
        Flow merge = ShippedFlows.Merge;
        int visits = ShippedFlows.MergePendingCap + ShippedFlows.MergeResumes;
        FlowRunPosition position = new(
            "gated-merge",
            ImmutableDictionary.Create<string, int>(StringComparer.Ordinal)
                .SetItem("gated-merge", visits)
                .SetItem("wait-for-checks", ShippedFlows.MergePendingCap),
            0);
        using JsonDocument pending = JsonDocument.Parse("""{"verdict":"pending"}""");
        using JsonDocument merged = JsonDocument.Parse("""{"verdict":"merged"}""");

        // a resume past the lap looks once and pauses while checks still run
        Assert.IsType<FlowRunMove.Pause>(Interpreter.Decide(merge, position, pending));
        Assert.Equal(new FlowRunMove.End("merged"), Interpreter.Decide(merge, position, merged));
        Assert.True(Interpreter.CanFireAtNextVisit(merge.Step("gated-merge")!, visits - 1));
    }

    [Fact]
    public void The_review_on_assignment_flow_is_one_agent_step_whose_answers_both_end()
    {
        Flow review = ShippedFlows.ReviewOnAssignment;

        Assert.Single(review.Steps);
        FlowStep step = review.First!;
        Assert.Equal("reviewer-adversarial", step.Agent);
        Assert.Equal("review", step.AnswerSchema);
        Assert.Equal(Flow.End, Next(review, step.Name, "clean"));
        Assert.Equal(Flow.End, Next(review, step.Name, "not-clean"));
    }

    [Fact]
    public void Every_shipped_flow_declares_a_budget_and_a_spawn_cap_and_hashes_distinctly()
    {
        Assert.All(ShippedFlows.All, f => Assert.True(f.Budget > 0 && f.SpawnCap > 0));
        Assert.Equal(4, ShippedFlows.All.Select(f => f.Hash).Distinct().Count());
        Assert.Equal(ShippedFlows.All.Select(f => f.Hash), ShippedDefinitions.Flows.Select(d => d.ContentHash));
    }

    [Fact]
    public void The_shipped_content_reads_back_to_the_same_flow()
    {
        foreach (ShippedDefinition shipped in ShippedDefinitions.Flows)
        {
            Assert.Equal(DefinitionKind.Flow, shipped.Kind);
            Assert.Equal(shipped.ContentHash, Flow.FromJson(shipped.Content).Hash);
        }
    }
}
