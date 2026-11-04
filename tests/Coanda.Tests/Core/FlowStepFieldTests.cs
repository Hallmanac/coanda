using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

/// <summary>An agent step declares the parcel fields its context carries; a system step declares none.</summary>
public sealed class FlowStepFieldTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string> { "do-thing" };

    private static FlowTransition On(string verdict, string target) =>
        new() { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = 2, Target = target };

    private static Flow With(IReadOnlyList<string> judgeFields, IReadOnlyList<string>? actFields = null) => new()
    {
        Name = "sample",
        Accepts = ["task"],
        Outcome = "thing-done",
        Budget = 1m,
        SpawnCap = 2,
        Steps =
        [
            new FlowStep { Name = "judge", Agent = "an-agent", AnswerSchema = "a-schema", Fields = judgeFields, Transitions = [On("done", "act")] },
            new FlowStep { Name = "act", SystemStep = "do-thing", Fields = actFields ?? [], Transitions = [On("done", Flow.End)] },
        ],
    };

    [Fact]
    public void An_agent_step_declaring_plain_distinct_fields_loads()
    {
        FluentActions.Invoking(() => With(["objective", "non-goals", "pull_request2"]).Validate(Known)).Should().NotThrow();
    }

    [Fact]
    public void A_system_step_declaring_a_field_is_refused_quoting_the_rule()
    {
        FluentActions.Invoking(() => With([], actFields: ["objective"]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("System step 'act' of flow 'sample' declares fields; only an agent step declares the fields its context carries.");
    }

    [Fact]
    public void A_field_declared_twice_is_refused_quoting_the_rule()
    {
        FluentActions.Invoking(() => With(["objective", "criteria", "objective"]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Agent step 'judge' of flow 'sample' declares the field 'objective' twice; a step declares each field once.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("pull request")]
    [InlineData("objective:")]
    public void A_field_whose_name_is_not_plain_is_refused_quoting_the_rule(string name)
    {
        FluentActions.Invoking(() => With([name]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage($"Agent step 'judge' of flow 'sample' declares the field '{name}'; a field's name is plain: letters, digits, hyphens, and underscores, and not empty.");
    }

    [Fact]
    public void A_null_field_read_from_content_is_refused()
    {
        string content = Flow.CanonicalJson(With(["objective"])).Replace("\"objective\"", "null", StringComparison.Ordinal);

        FluentActions.Invoking(() => Flow.FromJson(content).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("*declares the field ''*");
    }

    [Fact]
    public void Declared_fields_survive_the_canonical_json_and_are_part_of_the_hash()
    {
        Flow declared = With(["objective", "criteria"]);

        Flow read = Flow.FromJson(Flow.CanonicalJson(declared));

        read.Steps[0].Fields.Should().Equal("objective", "criteria");
        read.Hash.Should().Be(declared.Hash);
        string[] hashes = [With([]).Hash, declared.Hash, With(["criteria", "objective"]).Hash, With(["objective"]).Hash];
        hashes.Should().OnlyHaveUniqueItems("the fields and their order are part of the hash");
    }

    [Fact]
    public void The_shipped_build_flow_briefs_its_agent_steps_with_the_objective_criteria_and_non_goals()
    {
        Flow build = ShippedFlows.Build;

        foreach (string step in new[] { "build", "fix", "adversarial-review", "conformance-review" })
        {
            build.Step(step)?.Fields.Should().Equal(["objective", "criteria", "non-goals"], $"step '{step}' is an agent step the task's brief informs");
        }

        build.Steps.Where(s => s.IsSystemStep).Should().OnlyContain(s => s.Fields.Count == 0);
        build.Validate(ShippedSystemSteps.Names);
    }

    [Fact]
    public void The_build_flow_of_the_gated_merge_without_its_declarations_hashes_to_a_literal()
    {
        Flow declared = PreviousFlows.BuildWithGatedMerge;
        Flow undeclared = declared with { Steps = [.. declared.Steps.Select(s => s with { Fields = [], Sources = [] })] };

        undeclared.Hash.Should().Be("aa5f4bb96eec30da", "a flow none of whose steps declares fields leaves fields out of its hash");
        declared.Hash.Should().NotBe(undeclared.Hash);
    }
}
