using System.Collections.Immutable;
using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.Contours;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

public sealed class ShippedContoursTests
{
    /// <summary>
    /// Routes the way the interpreter will, with equality only: the first transition of the step
    /// whose field equals the value. Test scaffolding only; the interpreter is a later task.
    /// </summary>
    private static string Next(Contour contour, string step, string verdict)
    {
        ContourStep from = contour.Step(step) ?? throw new InvalidOperationException($"No step '{step}'.");
        return from.Transitions
            .Single(t => t.Field == "verdict" && t.Value.GetString() == verdict)
            .Target;
    }

    private static List<string> Walk(Contour contour, Func<ContourStep, string> answer)
    {
        List<string> visited = [];
        string at = contour.First!.Name;
        while (at != Contour.End)
        {
            visited.Add(at);
            ContourStep step = contour.Step(at)!;
            at = Next(contour, at, answer(step));
        }

        return visited;
    }

    [Fact]
    public void The_four_shipped_contours_pass_validation_with_the_known_action_names()
    {
        ShippedContours.All.Select(f => f.Name).Should().Equal("build", "merge", "discovery", "review-on-assignment");
        foreach (Contour contour in ShippedContours.All)
        {
            contour.Validate(ShippedActions.Names);
        }
    }

    [Fact]
    public void The_shipped_actions_are_the_core_catalog_and_the_software_catalog()
    {
        ShippedActions.Names.Should().BeEquivalentTo(
        [
            "wait-for-human", "wait-for-external-event", "timer", "emit-outcome", "move-parcel", "derive-parcels",
            "worktree", "gates", "open-pr", "gated-merge", "rebase",
        ]);
    }

    [Fact]
    public void The_shipped_contours_accept_types_that_ship_and_declare_their_outcomes()
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
            Contour contour = ShippedContours.Find(name)!;
            contour.Accepts.Should().Equal(accepts);
            contour.Outcome.Should().Be(outcome);
            ShippedTypes.Find(accepts).Should().NotBeNull();
        }
    }

    [Fact]
    public void No_shipped_contour_holds_an_emit_outcome_step()
    {
        ShippedContours.All.SelectMany(f => f.Steps).Should().NotContain(s => s.Action == CoreActions.EmitOutcome);
    }

    [Fact]
    public void The_build_contour_on_done_and_clean_visits_the_steps_in_order_and_reaches_end()
    {
        Contour build = ShippedContours.Build;

        List<string> visited = Walk(build, step => step.Name switch
        {
            "adversarial-review" or "conformance-review" => "clean",
            "open-pr" => "opened",
            _ => "done",
        });

        List<string> actions = [.. visited.Select(name => build.Step(name)!.Action ?? build.Step(name)!.Agent!)];
        actions.Should().Equal("worktree", "build", "gates", "reviewer-adversarial", "reviewer-conformance", "open-pr", "move-parcel");
        build.Steps.Should().NotContain(s => s.Action == SoftwareActions.GatedMerge, "the merge is a contour of its own that follows pr-opened");
        build.First!.Name.Should().Be("worktree");
        build.Steps.Count(s => s.Action == CoreActions.MoveParcel).Should().Be(1);

        ContourStep move = build.Step(visited[^1])!;
        move.Arguments.Should().ContainSingle().Which.Key.Should().Be("state");
        move.Arguments["state"].GetString().Should().Be("review");
        ShippedTypes.Task.AllowsMove("build", "review").Should().BeTrue();
    }

    [Fact]
    public void In_the_build_contour_not_clean_from_either_review_and_failed_from_gates_lead_to_fix_and_fix_done_returns_to_gates()
    {
        Contour build = ShippedContours.Build;

        Next(build, "adversarial-review", "not-clean").Should().Be("fix");
        Next(build, "conformance-review", "not-clean").Should().Be("fix");
        Next(build, "gates", "failed").Should().Be("fix");
        Next(build, "fix", "done").Should().Be("gates");
        Next(build, "adversarial-review", "clean").Should().Be("conformance-review");
    }

    [Fact]
    public void In_the_build_contour_blocked_answers_pause()
    {
        Next(ShippedContours.Build, "build", "blocked").Should().Be(Contour.Pause);
        Next(ShippedContours.Build, "fix", "blocked").Should().Be(Contour.Pause);
    }

    [Fact]
    public void Review_to_fix_transitions_cap_at_three_and_every_other_build_transition_at_five()
    {
        Contour build = ShippedContours.Build;
        var reviews = new HashSet<string> { "adversarial-review", "conformance-review" };

        foreach (ContourStep step in build.Steps)
        {
            foreach (ContourTransition transition in step.Transitions)
            {
                int expected = reviews.Contains(step.Name) && transition.Target == "fix" ? 3 : 5;
                transition.VisitCap.Should().Be(expected, $"{step.Name} on {transition.Value} goes to {transition.Target}");
            }
        }
    }

    [Fact]
    public void The_build_contour_names_the_agents_and_schemas_the_agent_definitions_seed()
    {
        ShippedContours.Build.Steps.Where(s => s.IsJudgment).Select(s => (s.Name, s.Agent, s.AnswerSchema)).Should().Equal(
            ("build", "build", "build"),
            ("adversarial-review", "reviewer-adversarial", "review"),
            ("conformance-review", "reviewer-conformance", "review"),
            ("fix", "fix", "fix"));
    }

    [Fact]
    public void The_discovery_contour_has_one_judgment_that_derives_or_carries_forward()
    {
        Contour discovery = ShippedContours.Discovery;

        discovery.Steps.Count(s => s.IsJudgment).Should().Be(1);
        ContourStep judgment = discovery.First!;
        judgment.Agent.Should().Be("discovery");
        judgment.AnswerSchema.Should().Be("discovery");

        string derive = Next(discovery, judgment.Name, "derive");
        discovery.Step(derive)!.Action.Should().Be(CoreActions.DeriveParcels);
        Next(discovery, derive, "done").Should().Be(Contour.End);
        Next(discovery, judgment.Name, "carry-forward").Should().Be(Contour.End);
    }

    [Fact]
    public void The_merge_contour_follows_the_builds_pr_opened_and_closes_its_task_as_completed()
    {
        Contour merge = ShippedContours.Merge;

        merge.Follow.Should().BeEquivalentTo(new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = merge.Follow!.DepthCap });
        merge.Follow.DepthCap.Should().BePositive();
        merge.Closes.Should().Be("completed");
        ShippedTypes.Task.IsResolution(merge.Closes!).Should().BeTrue();
        merge.Steps.SelectMany(s => s.Transitions).Should().OnlyContain(t => t.VisitCap > 0);
        merge.First!.Action.Should().Be(SoftwareActions.GatedMerge);
        Next(merge, "gated-merge", "merged").Should().Be(Contour.End);
    }

    [Fact]
    public void The_merge_contours_lap_waits_at_least_twenty_minutes_with_its_rebase_laps_taken_out_before_it_pauses_and_a_late_merge_still_ends()
    {
        Contour merge = ShippedContours.Merge;
        ContourStep gated = merge.Step("gated-merge")!;
        ContourTransition pending = gated.Transitions.Single(t => t.Value.GetString() == "pending");
        ContourTransition merged = gated.Transitions.Single(t => t.Value.GetString() == "merged");
        ContourStep timer = merge.Step(pending.Target)!;
        ContourTransition back = timer.Transitions.Should().ContainSingle().Subject;
        ContourTransition rebaseLaps = RebaseLapCap(merge);

        timer.Action.Should().Be(CoreActions.Timer);
        back.Target.Should().Be("gated-merge");
        double seconds = timer.Arguments["seconds"].GetDouble();
        (seconds * (pending.VisitCap - rebaseLaps.VisitCap)).Should().BeGreaterThanOrEqualTo(1200, "the lap waits at least twenty minutes in all once the rebase laps' visits are taken out");
        back.VisitCap.Should().BeGreaterThanOrEqualTo(pending.VisitCap, "the timer never pauses the lap before the pending cap does");
        merged.VisitCap.Should().BeGreaterThanOrEqualTo(pending.VisitCap + 100, "a merge on the lap's last visit or on any of a hundred resumes still ends the flow run");
        merged.VisitCap.Should().BeGreaterThanOrEqualTo(pending.VisitCap + 5);
    }

    [Fact]
    public void The_merge_contour_routes_a_behind_head_through_a_worktree_and_a_rebase_back_to_the_timer_with_no_judgment()
    {
        Contour merge = ShippedContours.Merge;
        ContourStep gated = merge.Step("gated-merge")!;
        ContourTransition pending = gated.Transitions.Single(t => t.Value.GetString() == "pending");
        ContourTransition behind = gated.Transitions.Single(t => t.Value.GetString() == "behind");
        ContourStep worktree = merge.Step(behind.Target)!;
        ContourTransition checkedOut = worktree.Transitions.Should().ContainSingle().Subject;
        ContourStep rebase = merge.Step(checkedOut.Target)!;
        ContourTransition rebased = rebase.Transitions.Should().ContainSingle().Subject;

        worktree.Action.Should().Be(SoftwareActions.Worktree);
        checkedOut.Value.GetString().Should().Be("done");
        rebase.Action.Should().Be(SoftwareActions.Rebase);
        rebased.Value.GetString().Should().Be("done");
        rebased.Target.Should().Be(pending.Target, "the rebase waits one timer period before the pull request is inspected again");
        merge.Steps.Should().NotContain(s => s.IsJudgment, "the lap runs no judgment and re-runs no review");
        behind.VisitCap.Should().Be(pending.VisitCap, "a head is rebased on any visit the pending cap still allows");
        checkedOut.VisitCap.Should().BeGreaterThanOrEqualTo(1).And.BeLessThan(pending.VisitCap);
        rebased.VisitCap.Should().BeGreaterThanOrEqualTo(checkedOut.VisitCap);
    }

    [Fact]
    public void The_merge_contours_rebase_laps_pause_on_the_worktree_steps_cap_once_spent_before_the_rebase()
    {
        Contour merge = ShippedContours.Merge;
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
        Interpreter.Decide(merge, position, done).Should().BeOfType<FlowRunMove.Pause>()
            .Which.Reason.Should().Be($"Step 'worktree' reached the visit cap of {laps} on its transition to 'rebase'.");

        FlowRunPosition Go(FlowRunPosition from, JsonDocument answer, string step)
        {
            FlowRunMove move = Interpreter.Decide(merge, from, answer);
            move.Should().Be(new FlowRunMove.GoTo(step));
            return Interpreter.Apply(merge, from, (FlowRunMove.GoTo)move);
        }
    }

    [Fact]
    public void The_build_discovery_and_review_on_assignment_contours_hash_as_they_did_before_the_rebase_lap()
    {
        ShippedContours.Build.Hash.Should().Be("b04d6a7f9787b3ba");
        ShippedContours.Discovery.Hash.Should().Be("d7e57364f965bce0");
        ShippedContours.ReviewOnAssignment.Hash.Should().Be("895e023427d7e4a3");
    }

    /// <summary>The visit cap on the merge's worktree step's transition to the rebase, which bounds the rebase laps.</summary>
    private static ContourTransition RebaseLapCap(Contour merge)
    {
        ContourTransition behind = merge.Step("gated-merge")!.Transitions.Single(t => t.Value.GetString() == "behind");
        return merge.Step(behind.Target)!.Transitions.Should().ContainSingle().Subject;
    }

    [Fact]
    public void The_merge_contours_pending_cap_pauses_the_flow_run_once_spent_rather_than_waiting_again()
    {
        Contour merge = ShippedContours.Merge;
        int cap = ShippedContours.MergePendingCap;
        FlowRunPosition position = Interpreter.Start(merge);
        using JsonDocument pending = JsonDocument.Parse("""{"verdict":"pending"}""");
        using JsonDocument done = JsonDocument.Parse("""{"verdict":"done"}""");

        for (int lap = 1; lap <= cap; lap++)
        {
            FlowRunMove wait = Interpreter.Decide(merge, position, pending);
            wait.Should().Be(new FlowRunMove.GoTo("wait-for-checks"), $"lap {lap} is within the pending cap");
            position = Interpreter.Apply(merge, position, (FlowRunMove.GoTo)wait);
            FlowRunMove again = Interpreter.Decide(merge, position, done);
            again.Should().Be(new FlowRunMove.GoTo("gated-merge"));
            position = Interpreter.Apply(merge, position, (FlowRunMove.GoTo)again);
        }

        Interpreter.Decide(merge, position, pending).Should().BeOfType<FlowRunMove.Pause>()
            .Which.Reason.Should().Contain($"visit cap of {cap}");
        using JsonDocument merged = JsonDocument.Parse("""{"verdict":"merged"}""");
        Interpreter.Decide(merge, position, merged).Should().Be(new FlowRunMove.End("merged"), "a merge on the lap's last visit ends the flow run");
    }

    [Fact]
    public void A_merge_on_the_hundredth_resume_after_the_lap_pauses_still_ends_the_flow_run()
    {
        Contour merge = ShippedContours.Merge;
        int visits = ShippedContours.MergePendingCap + ShippedContours.MergeResumes;
        FlowRunPosition position = new(
            "gated-merge",
            ImmutableDictionary.Create<string, int>(StringComparer.Ordinal)
                .SetItem("gated-merge", visits)
                .SetItem("wait-for-checks", ShippedContours.MergePendingCap),
            0);
        using JsonDocument pending = JsonDocument.Parse("""{"verdict":"pending"}""");
        using JsonDocument merged = JsonDocument.Parse("""{"verdict":"merged"}""");

        Interpreter.Decide(merge, position, pending).Should().BeOfType<FlowRunMove.Pause>("a resume past the lap looks once and pauses while checks still run");
        Interpreter.Decide(merge, position, merged).Should().Be(new FlowRunMove.End("merged"));
        Interpreter.CanFireAtNextVisit(merge.Step("gated-merge")!, visits - 1).Should().BeTrue();
    }

    [Fact]
    public void The_review_on_assignment_contour_is_one_judgment_whose_answers_both_end()
    {
        Contour review = ShippedContours.ReviewOnAssignment;

        review.Steps.Should().ContainSingle();
        ContourStep step = review.First!;
        step.Agent.Should().Be("reviewer-adversarial");
        step.AnswerSchema.Should().Be("review");
        Next(review, step.Name, "clean").Should().Be(Contour.End);
        Next(review, step.Name, "not-clean").Should().Be(Contour.End);
    }

    [Fact]
    public void Every_shipped_contour_declares_a_budget_and_a_spawn_cap_and_hashes_distinctly()
    {
        ShippedContours.All.Should().OnlyContain(f => f.Budget > 0 && f.SpawnCap > 0);
        ShippedContours.All.Select(f => f.Hash).Distinct().Should().HaveCount(4);
        ShippedDefinitions.Contours.Select(d => d.ContentHash).Should().Equal(ShippedContours.All.Select(f => f.Hash));
    }

    [Fact]
    public void The_shipped_content_reads_back_to_the_same_contour()
    {
        foreach (ShippedDefinition shipped in ShippedDefinitions.Contours)
        {
            shipped.Kind.Should().Be(DefinitionKind.Contour);
            Contour.FromJson(shipped.Content).Hash.Should().Be(shipped.ContentHash);
        }
    }
}
