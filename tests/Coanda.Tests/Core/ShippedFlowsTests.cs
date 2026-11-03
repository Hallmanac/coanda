using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ShippedFlowsTests
{
    /// <summary>
    /// Routes the way the interpreter will, with equality only: the first transition of the step
    /// whose field equals the value. Test scaffolding only; the interpreter is a later task.
    /// </summary>
    private static string Next(FlowDefinition flow, string step, string verdict)
    {
        FlowStep from = flow.Step(step) ?? throw new InvalidOperationException($"No step '{step}'.");
        return from.Transitions
            .Single(t => t.Field == "verdict" && t.Value.GetString() == verdict)
            .Target;
    }

    private static List<string> Walk(FlowDefinition flow, Func<FlowStep, string> answer)
    {
        List<string> visited = [];
        string at = flow.First!.Name;
        while (at != FlowDefinition.End)
        {
            visited.Add(at);
            FlowStep step = flow.Step(at)!;
            at = Next(flow, at, answer(step));
        }

        return visited;
    }

    [Fact]
    public void The_three_shipped_flows_pass_validation_with_the_known_action_names()
    {
        ShippedFlows.All.Select(f => f.Name).Should().Equal("build", "discovery", "review-on-assignment");
        foreach (FlowDefinition flow in ShippedFlows.All)
        {
            flow.Validate(DefinitionDecider.KnownActions);
        }
    }

    [Fact]
    public void The_known_actions_are_the_core_catalog_and_the_software_catalog()
    {
        DefinitionDecider.KnownActions.Should().BeEquivalentTo(
        [
            "spawn", "wait-for-human", "wait-for-external-event", "timer", "emit-outcome", "move-parcel", "derive-parcels",
            "worktree", "gates", "open-pr", "gated-merge", "rebase",
        ]);
    }

    [Fact]
    public void The_shipped_flows_accept_types_that_ship_and_declare_their_outcomes()
    {
        (string Name, string Accepts, string Outcome)[] expected =
        [
            ("build", "task", "pr-opened"),
            ("discovery", "idea", "parcels-derived"),
            ("review-on-assignment", "task", "report-produced"),
        ];

        foreach ((string name, string accepts, string outcome) in expected)
        {
            FlowDefinition flow = ShippedFlows.Find(name)!;
            flow.Accepts.Should().Equal(accepts);
            flow.Outcome.Should().Be(outcome);
            ShippedTypes.Find(accepts).Should().NotBeNull();
        }
    }

    [Fact]
    public void No_shipped_flow_holds_an_emit_outcome_step()
    {
        ShippedFlows.All.SelectMany(f => f.Steps).Should().NotContain(s => s.Action == CoreActions.EmitOutcome);
    }

    [Fact]
    public void The_build_flow_on_done_and_clean_visits_the_steps_in_order_and_reaches_end()
    {
        FlowDefinition build = ShippedFlows.Build;

        List<string> visited = Walk(build, step => step.Name is "adversarial-review" or "conformance-review" ? "clean" : "done");

        List<string> actions = [.. visited.Select(name => build.Step(name)!.Action ?? build.Step(name)!.Agent!)];
        actions.Should().Equal("worktree", "build", "gates", "reviewer-adversarial", "reviewer-conformance", "open-pr", "gated-merge", "move-parcel");
        build.First!.Name.Should().Be("worktree");
        build.Steps.Count(s => s.Action == CoreActions.MoveParcel).Should().Be(1);

        FlowStep move = build.Step(visited[^1])!;
        move.Arguments.Should().ContainSingle().Which.Key.Should().Be("state");
        move.Arguments["state"].GetString().Should().Be("review");
        ShippedTypes.Task.AllowsMove("build", "review").Should().BeTrue();
    }

    [Fact]
    public void In_the_build_flow_not_clean_from_either_review_and_failed_from_gates_lead_to_fix_and_fix_done_returns_to_gates()
    {
        FlowDefinition build = ShippedFlows.Build;

        Next(build, "adversarial-review", "not-clean").Should().Be("fix");
        Next(build, "conformance-review", "not-clean").Should().Be("fix");
        Next(build, "gates", "failed").Should().Be("fix");
        Next(build, "fix", "done").Should().Be("gates");
        Next(build, "adversarial-review", "clean").Should().Be("conformance-review");
    }

    [Fact]
    public void In_the_build_flow_blocked_answers_park()
    {
        Next(ShippedFlows.Build, "build", "blocked").Should().Be(FlowDefinition.Park);
        Next(ShippedFlows.Build, "fix", "blocked").Should().Be(FlowDefinition.Park);
    }

    [Fact]
    public void Review_to_fix_transitions_cap_at_three_and_every_other_build_transition_at_five()
    {
        FlowDefinition build = ShippedFlows.Build;
        var reviews = new HashSet<string> { "adversarial-review", "conformance-review" };

        foreach (FlowStep step in build.Steps)
        {
            foreach (FlowTransition transition in step.Transitions)
            {
                int expected = reviews.Contains(step.Name) && transition.Target == "fix" ? 3 : 5;
                transition.VisitCap.Should().Be(expected, $"{step.Name} on {transition.Value} goes to {transition.Target}");
            }
        }
    }

    [Fact]
    public void The_build_flow_names_the_agents_and_schemas_the_agent_definitions_seed()
    {
        ShippedFlows.Build.Steps.Where(s => s.IsJudgment).Select(s => (s.Name, s.Agent, s.AnswerSchema)).Should().Equal(
            ("build", "build", "build"),
            ("adversarial-review", "reviewer-adversarial", "review"),
            ("conformance-review", "reviewer-conformance", "review"),
            ("fix", "fix", "fix"));
    }

    [Fact]
    public void The_discovery_flow_has_one_judgment_that_derives_or_carries_forward()
    {
        FlowDefinition discovery = ShippedFlows.Discovery;

        discovery.Steps.Count(s => s.IsJudgment).Should().Be(1);
        FlowStep judgment = discovery.First!;
        judgment.Agent.Should().Be("discovery");
        judgment.AnswerSchema.Should().Be("discovery");

        string derive = Next(discovery, judgment.Name, "derive");
        discovery.Step(derive)!.Action.Should().Be(CoreActions.DeriveParcels);
        Next(discovery, derive, "done").Should().Be(FlowDefinition.End);
        Next(discovery, judgment.Name, "carry-forward").Should().Be(FlowDefinition.End);
    }

    [Fact]
    public void The_review_on_assignment_flow_is_one_judgment_whose_answers_both_end()
    {
        FlowDefinition review = ShippedFlows.ReviewOnAssignment;

        review.Steps.Should().ContainSingle();
        FlowStep step = review.First!;
        step.Agent.Should().Be("reviewer-adversarial");
        step.AnswerSchema.Should().Be("review");
        Next(review, step.Name, "clean").Should().Be(FlowDefinition.End);
        Next(review, step.Name, "not-clean").Should().Be(FlowDefinition.End);
    }

    [Fact]
    public void Every_shipped_flow_declares_a_budget_and_a_spawn_cap_and_hashes_distinctly()
    {
        ShippedFlows.All.Should().OnlyContain(f => f.Budget > 0 && f.SpawnCap > 0);
        ShippedFlows.All.Select(f => f.Hash).Distinct().Should().HaveCount(3);
        ShippedDefinition.ShippedFlowDefinitions.Select(d => d.ContentHash).Should().Equal(ShippedFlows.All.Select(f => f.Hash));
    }

    [Fact]
    public void The_shipped_content_reads_back_to_the_same_flow()
    {
        foreach (ShippedDefinition shipped in ShippedDefinition.ShippedFlowDefinitions)
        {
            shipped.Kind.Should().Be(DefinitionKind.Flow);
            FlowDefinition.FromJson(shipped.Content).Hash.Should().Be(shipped.ContentHash);
        }
    }
}
