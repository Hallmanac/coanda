using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Contours;

namespace Coanda.Tests.Core;

public sealed class ContourTests
{
    // Validation is handed the known names; these are made up so the contour shape is shown to hold no catalog.
    private static readonly IReadOnlySet<string> Known = new HashSet<string> { "do-thing", "move-thing" };

    private static ContourTransition On(string verdict, string target, int cap = 2) =>
        new() { Field = "verdict", Value = ContourValue.Of(verdict), VisitCap = cap, Target = target };

    private static ContourStep Judge(string name = "judge") => new()
    {
        Name = name,
        Agent = "an-agent",
        AnswerSchema = "a-schema",
        Transitions = [On("done", "act")],
    };

    private static ContourStep Act(string name = "act") => new()
    {
        Name = name,
        Action = "do-thing",
        Arguments = new Dictionary<string, JsonElement> { ["state"] = ContourValue.Of("review"), ["count"] = ContourValue.Of(2) },
        Transitions = [On("done", Contour.End), On("failed", "judge")],
    };

    private static Contour Valid() => new()
    {
        Name = "sample",
        Accepts = ["task"],
        Outcome = "thing-done",
        Budget = 4.5m,
        SpawnCap = 6,
        Steps = [Judge(), Act()],
    };

    private static Contour WithFirstStep(Func<ContourStep, ContourStep> change) =>
        Valid() with { Steps = [change(Judge()), Act()] };

    private static Contour WithActStep(Func<ContourStep, ContourStep> change) =>
        Valid() with { Steps = [Judge(), change(Act())] };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public static TheoryData<string, Contour, string> Refusals => new()
    {
        { "no name", Valid() with { Name = " " }, "A contour needs a name." },
        { "no accepted type", Valid() with { Accepts = [] }, "needs at least one accepted parcel type" },
        { "a blank accepted type", Valid() with { Accepts = [" "] }, "needs at least one accepted parcel type" },
        { "no outcome", Valid() with { Outcome = "" }, "needs an outcome" },
        { "missing budget", Valid() with { Budget = 0 }, "needs a budget greater than zero" },
        { "negative budget", Valid() with { Budget = -1m }, "needs a budget greater than zero" },
        { "missing spawn cap", Valid() with { SpawnCap = 0 }, "needs a spawn cap greater than zero" },
        { "negative spawn cap", Valid() with { SpawnCap = -3 }, "needs a spawn cap greater than zero" },
        { "no steps", Valid() with { Steps = [] }, "needs at least one step" },
        { "a step with no name", Valid() with { Steps = [Judge(""), Act()] }, "has a step with no name" },
        { "two steps with one name", Valid() with { Steps = [Judge(), Act("judge")] }, "has two steps named 'judge'" },
        { "a step named pause", Valid() with { Steps = [Judge("pause"), Act()] }, "has a step named 'pause'" },
        { "a step named end", Valid() with { Steps = [Judge(), Act("end")] }, "has a step named 'end'" },
        { "an agent and an action", WithFirstStep(s => s with { Action = "do-thing" }), "must name an agent (a judgment) or an action, and exactly one of them" },
        { "neither agent nor action", WithFirstStep(s => s with { Agent = null }), "must name an agent (a judgment) or an action, and exactly one of them" },
        { "a judgment without a schema", WithFirstStep(s => s with { AnswerSchema = null }), "needs an agent and an answer schema" },
        { "a judgment with a blank schema", WithFirstStep(s => s with { AnswerSchema = " " }), "needs an agent and an answer schema" },
        { "an unknown action", WithActStep(s => s with { Action = "worktree" }), "names the action 'worktree', which is not a known action" },
        { "an argument with a spaced name", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a name"] = ContourValue.Of("x") } }), "an argument name is plain" },
        { "an argument with a dotted name", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a.b"] = ContourValue.Of("x") } }), "an argument name is plain" },
        { "an object argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("""{"x":1}""") } }), "Argument 'a' of step 'act' in contour 'sample' must be a JSON string, number, or boolean." },
        { "an array argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("[1]") } }), "must be a JSON string, number, or boolean" },
        { "a null argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("null") } }), "must be a JSON string, number, or boolean" },
        { "no transitions", WithFirstStep(s => s with { Transitions = [] }), "needs at least one transition" },
        { "a missing visit cap", WithFirstStep(s => s with { Transitions = [On("done", "act", 0)] }), "needs a visit cap greater than zero" },
        { "a negative visit cap", WithFirstStep(s => s with { Transitions = [On("done", "act", -1)] }), "needs a visit cap greater than zero" },
        { "a target that is not a step", WithFirstStep(s => s with { Transitions = [On("done", "nowhere")] }), "targets 'nowhere', which is not a step of the contour, pause, or end" },
        { "an answer field that is not plain", WithFirstStep(s => s with { Transitions = [new ContourTransition { Field = "a == b", Value = ContourValue.Of("x"), VisitCap = 1, Target = "act" }] }), "an answer field is plain" },
        { "a blank answer field", WithFirstStep(s => s with { Transitions = [new ContourTransition { Value = ContourValue.Of("x"), VisitCap = 1, Target = "act" }] }), "an answer field is plain" },
        { "an object value", WithFirstStep(s => s with { Transitions = [new ContourTransition { Field = "verdict", Value = Json("{}"), VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
        { "a null value", WithFirstStep(s => s with { Transitions = [new ContourTransition { Field = "verdict", Value = Json("null"), VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
        { "a follow with a blank outcome", Valid() with { Follow = new ContourFollow { Outcome = " ", DepthCap = 2 } }, "its follow needs the outcome's name" },
        { "a follow with no outcome", Valid() with { Follow = new ContourFollow { DepthCap = 2 } }, "its follow needs the outcome's name" },
        { "a follow naming a contour that is not plain", Valid() with { Follow = new ContourFollow { Outcome = "pr-opened", Contour = "build or fix", DepthCap = 2 } }, "a followed contour's name is plain" },
        { "a follow naming a blank contour", Valid() with { Follow = new ContourFollow { Outcome = "pr-opened", Contour = "", DepthCap = 2 } }, "a followed contour's name is plain" },
        { "a follow with no depth cap", Valid() with { Follow = new ContourFollow { Outcome = "pr-opened" } }, "its follow needs a depth cap greater than zero" },
        { "a follow with a negative depth cap", Valid() with { Follow = new ContourFollow { Outcome = "pr-opened", DepthCap = -1 } }, "its follow needs a depth cap greater than zero" },
        { "a missing value", WithFirstStep(s => s with { Transitions = [new ContourTransition { Field = "verdict", VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
    };

    [Fact]
    public void A_valid_contour_passes_and_pause_and_end_are_valid_targets()
    {
        Contour contour = Valid() with
        {
            Steps = [Judge() with { Transitions = [On("done", "act"), On("stuck", Contour.Pause), On("skip", Contour.End)] }, Act()],
        };

        FluentActions.Invoking(() => contour.Validate(Known)).Should().NotThrow();
        contour.First!.Name.Should().Be("judge");
        contour.Step("act").Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_contour_that_breaks_a_rule_is_refused_quoting_it(string rule, Contour contour, string message)
    {
        rule.Should().NotBeNullOrEmpty();
        FluentActions.Invoking(() => contour.Validate(Known))
            .Should().Throw<DomainException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Validation_takes_the_known_actions_as_an_input()
    {
        FluentActions.Invoking(() => Valid().Validate(new HashSet<string> { "other" }))
            .Should().Throw<DomainException>().WithMessage("*names the action 'do-thing'*");
    }

    [Fact]
    public void Plain_names_are_letters_digits_hyphens_and_underscores()
    {
        Contour.IsPlainName("verdict").Should().BeTrue();
        Contour.IsPlainName("Not-Clean_2").Should().BeTrue();
        Contour.IsPlainName("").Should().BeFalse();
        Contour.IsPlainName(null).Should().BeFalse();
        Contour.IsPlainName("a b").Should().BeFalse();
        Contour.IsPlainName("a.b").Should().BeFalse();
        Contour.IsPlainName("a==b").Should().BeFalse();
    }

    [Fact]
    public void Content_round_trips_to_a_contour_with_the_same_hash()
    {
        Contour contour = Valid();

        Contour read = Contour.FromJson(Contour.CanonicalJson(contour));

        read.Hash.Should().Be(contour.Hash);
        read.Validate(Known);
    }

    [Theory]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"s","Agent":"a","AnswerSchema":"b","Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"end","Condition":"cost < 5"}]}]}""", "Condition")]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"s","Agent":"a","AnswerSchema":"b","Expression":"x","Transitions":[]}]}""", "Expression")]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Retries":3,"Steps":[]}""", "Retries")]
    public void Content_carrying_a_property_the_shape_does_not_define_is_refused_naming_it(string content, string property)
    {
        FluentActions.Invoking(() => Contour.FromJson(content))
            .Should().Throw<DomainException>().WithMessage($"The content cannot be read as a contour*{property}*");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public void Content_that_is_not_a_contour_is_refused(string content)
    {
        FluentActions.Invoking(() => Contour.FromJson(content))
            .Should().Throw<DomainException>().WithMessage("The content*contour*");
    }

    [Fact]
    public void Content_missing_properties_reads_and_is_refused_by_validation_naming_the_rule()
    {
        Contour read = Contour.FromJson("{}");

        FluentActions.Invoking(() => read.Validate(Known))
            .Should().Throw<DomainException>().WithMessage("A contour needs a name.");
    }

    [Fact]
    public void Two_contours_built_separately_from_the_same_data_hash_equal()
    {
        Valid().Hash.Should().Be(Valid().Hash);
        Valid().Hash.Should().HaveLength(16);
    }

    [Fact]
    public void Changing_the_budget_a_visit_cap_a_target_or_an_argument_changes_the_hash()
    {
        string baseline = Valid().Hash;

        (Valid() with { Budget = 5m }).Hash.Should().NotBe(baseline);
        (Valid() with { SpawnCap = 7 }).Hash.Should().NotBe(baseline);
        WithFirstStep(s => s with { Transitions = [On("done", "act", 3)] }).Hash.Should().NotBe(baseline);
        WithActStep(s => s with { Transitions = [On("done", Contour.Pause), On("failed", "judge")] }).Hash.Should().NotBe(baseline);
        WithActStep(s => s with
        {
            Arguments = new Dictionary<string, JsonElement> { ["state"] = ContourValue.Of("build"), ["count"] = ContourValue.Of(2) },
        }).Hash.Should().NotBe(baseline);
    }

    [Fact]
    public void A_valid_follow_passes_and_round_trips_with_its_hash()
    {
        Contour follower = Valid() with { Follow = new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 2 } };

        FluentActions.Invoking(() => follower.Validate(Known)).Should().NotThrow();
        (Valid() with { Follow = new ContourFollow { Outcome = "pr-opened", DepthCap = 1 } }).Validate(Known);
        Contour read = Contour.FromJson(Contour.CanonicalJson(follower));
        read.Follow.Should().Be(follower.Follow);
        read.Hash.Should().Be(follower.Hash);
    }

    [Fact]
    public void Adding_a_follow_or_changing_its_depth_cap_changes_the_hash()
    {
        string baseline = Valid().Hash;
        var follow = new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 2 };
        Contour follower = Valid() with { Follow = follow };

        follower.Hash.Should().NotBe(baseline);
        (Valid() with { Follow = follow with { DepthCap = 3 } }).Hash.Should().NotBe(follower.Hash);
        (Valid() with { Follow = follow with { Contour = null } }).Hash.Should().NotBe(follower.Hash);
        (Valid() with { Follow = follow with { Outcome = "report-produced" } }).Hash.Should().NotBe(follower.Hash);
    }

    [Fact]
    public void A_contour_without_a_follow_writes_no_follow_into_its_content()
    {
        Contour.CanonicalJson(Valid()).Should().NotContain("Follow");
    }

    [Fact]
    public void The_hash_does_not_depend_on_the_order_arguments_were_written_in()
    {
        Contour reordered = WithActStep(s => s with
        {
            Arguments = new Dictionary<string, JsonElement> { ["count"] = ContourValue.Of(2), ["state"] = ContourValue.Of("review") },
        });

        reordered.Hash.Should().Be(Valid().Hash);
    }

    [Fact]
    public void A_small_fixed_contour_hashes_to_a_literal()
    {
        var contour = new Contour
        {
            Name = "tiny",
            Accepts = ["task"],
            Outcome = "report-produced",
            Budget = 2m,
            SpawnCap = 1,
            Steps =
            [
                new ContourStep
                {
                    Name = "look",
                    Agent = "reviewer",
                    AnswerSchema = "review",
                    Transitions = [On("clean", Contour.End, 1), On("not-clean", Contour.Pause, 1)],
                },
            ],
        };

        contour.Hash.Should().Be("91a9dc95e3c8a460");
    }

    [Fact]
    public void The_contour_shape_and_its_validation_name_no_software_action()
    {
        string source = File.ReadAllText(SourceFile("Contours", "Contour.cs"));

        foreach (string software in new[] { "worktree", "gates", "open-pr", "gated-merge", "rebase", "SoftwareActions" })
        {
            source.Should().NotContain(software);
        }
    }

    private static string SourceFile(string folder, string file)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "src", "Coanda.Core")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new InvalidOperationException("The repository root was not found."), "src", "Coanda.Core", folder, file);
    }
}
