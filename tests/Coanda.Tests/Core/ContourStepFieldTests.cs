using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

/// <summary>A judgment step declares the parcel fields its context carries; an action step declares none.</summary>
public sealed class ContourStepFieldTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string> { "do-thing" };

    private static ContourTransition On(string verdict, string target) =>
        new() { Field = "verdict", Value = ContourValue.Of(verdict), VisitCap = 2, Target = target };

    private static Contour With(IReadOnlyList<string> judgeFields, IReadOnlyList<string>? actFields = null) => new()
    {
        Name = "sample",
        Accepts = ["task"],
        Outcome = "thing-done",
        Budget = 1m,
        SpawnCap = 2,
        Steps =
        [
            new ContourStep { Name = "judge", Agent = "an-agent", AnswerSchema = "a-schema", Fields = judgeFields, Transitions = [On("done", "act")] },
            new ContourStep { Name = "act", Action = "do-thing", Fields = actFields ?? [], Transitions = [On("done", Contour.End)] },
        ],
    };

    [Fact]
    public void A_judgment_declaring_plain_distinct_fields_loads()
    {
        FluentActions.Invoking(() => With(["objective", "non-goals", "pull_request2"]).Validate(Known)).Should().NotThrow();
    }

    [Fact]
    public void An_action_step_declaring_a_field_is_refused_quoting_the_rule()
    {
        FluentActions.Invoking(() => With([], actFields: ["objective"]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Action step 'act' of contour 'sample' declares fields; only a judgment step declares the fields its context carries.");
    }

    [Fact]
    public void A_field_declared_twice_is_refused_quoting_the_rule()
    {
        FluentActions.Invoking(() => With(["objective", "criteria", "objective"]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of contour 'sample' declares the field 'objective' twice; a step declares each field once.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("pull request")]
    [InlineData("objective:")]
    public void A_field_whose_name_is_not_plain_is_refused_quoting_the_rule(string name)
    {
        FluentActions.Invoking(() => With([name]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage($"Judgment step 'judge' of contour 'sample' declares the field '{name}'; a field's name is plain: letters, digits, hyphens, and underscores, and not empty.");
    }

    [Fact]
    public void A_null_field_read_from_content_is_refused()
    {
        string content = Contour.CanonicalJson(With(["objective"])).Replace("\"objective\"", "null", StringComparison.Ordinal);

        FluentActions.Invoking(() => Contour.FromJson(content).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("*declares the field ''*");
    }

    [Fact]
    public void Declared_fields_survive_the_canonical_json_and_are_part_of_the_hash()
    {
        Contour declared = With(["objective", "criteria"]);

        Contour read = Contour.FromJson(Contour.CanonicalJson(declared));

        read.Steps[0].Fields.Should().Equal("objective", "criteria");
        read.Hash.Should().Be(declared.Hash);
        string[] hashes = [With([]).Hash, declared.Hash, With(["criteria", "objective"]).Hash, With(["objective"]).Hash];
        hashes.Should().OnlyHaveUniqueItems("the fields and their order are part of the hash");
    }

    [Fact]
    public void The_shipped_build_contour_briefs_its_judgments_with_the_objective_criteria_and_non_goals()
    {
        Contour build = ShippedContours.Build;

        foreach (string step in new[] { "build", "fix", "adversarial-review", "conformance-review" })
        {
            build.Step(step)?.Fields.Should().Equal(["objective", "criteria", "non-goals"], $"step '{step}' is a judgment the task's brief informs");
        }

        build.Steps.Where(s => s.IsAction).Should().OnlyContain(s => s.Fields.Count == 0);
        build.Validate(ShippedActions.Names);
    }

    [Fact]
    public void The_build_contour_of_the_gated_merge_without_its_declarations_hashes_as_it_did_before_fields_existed()
    {
        Contour declared = PreviousContours.BuildWithGatedMerge;
        Contour undeclared = declared with { Steps = [.. declared.Steps.Select(s => s with { Fields = [], Sources = [] })] };

        undeclared.Hash.Should().Be("dc6aa9adf2fb3c4f", "a contour none of whose steps declares fields hashes as before");
        declared.Hash.Should().NotBe(undeclared.Hash);
    }
}
