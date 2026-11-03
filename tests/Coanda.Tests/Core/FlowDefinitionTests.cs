using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;

namespace Coanda.Tests.Core;

public sealed class FlowDefinitionTests
{
    // Validation is handed the known names; these are made up so the flow shape is shown to hold no catalog.
    private static readonly IReadOnlySet<string> Known = new HashSet<string> { "do-thing", "move-thing" };

    private static FlowTransition On(string verdict, string target, int cap = 2) =>
        new() { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = cap, Target = target };

    private static FlowStep Judge(string name = "judge") => new()
    {
        Name = name,
        Agent = "an-agent",
        AnswerSchema = "a-schema",
        Transitions = [On("done", "act")],
    };

    private static FlowStep Act(string name = "act") => new()
    {
        Name = name,
        Action = "do-thing",
        Arguments = new Dictionary<string, JsonElement> { ["state"] = FlowValue.Of("review"), ["count"] = FlowValue.Of(2) },
        Transitions = [On("done", FlowDefinition.End), On("failed", "judge")],
    };

    private static FlowDefinition Valid() => new()
    {
        Name = "sample",
        Accepts = ["task"],
        Outcome = "thing-done",
        Budget = 4.5m,
        SpawnCap = 6,
        Steps = [Judge(), Act()],
    };

    private static FlowDefinition WithFirstStep(Func<FlowStep, FlowStep> change) =>
        Valid() with { Steps = [change(Judge()), Act()] };

    private static FlowDefinition WithActStep(Func<FlowStep, FlowStep> change) =>
        Valid() with { Steps = [Judge(), change(Act())] };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public static TheoryData<string, FlowDefinition, string> Refusals => new()
    {
        { "no name", Valid() with { Name = " " }, "A flow needs a name." },
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
        { "a step named park", Valid() with { Steps = [Judge("park"), Act()] }, "has a step named 'park'" },
        { "a step named end", Valid() with { Steps = [Judge(), Act("end")] }, "has a step named 'end'" },
        { "an agent and an action", WithFirstStep(s => s with { Action = "do-thing" }), "must name an agent (a judgment) or an action, and exactly one of them" },
        { "neither agent nor action", WithFirstStep(s => s with { Agent = null }), "must name an agent (a judgment) or an action, and exactly one of them" },
        { "a judgment without a schema", WithFirstStep(s => s with { AnswerSchema = null }), "needs an agent and an answer schema" },
        { "a judgment with a blank schema", WithFirstStep(s => s with { AnswerSchema = " " }), "needs an agent and an answer schema" },
        { "an unknown action", WithActStep(s => s with { Action = "worktree" }), "names the action 'worktree', which is not a known action" },
        { "an argument with a spaced name", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a name"] = FlowValue.Of("x") } }), "an argument name is plain" },
        { "an argument with a dotted name", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a.b"] = FlowValue.Of("x") } }), "an argument name is plain" },
        { "an object argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("""{"x":1}""") } }), "Argument 'a' of step 'act' in flow 'sample' must be a JSON string, number, or boolean." },
        { "an array argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("[1]") } }), "must be a JSON string, number, or boolean" },
        { "a null argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("null") } }), "must be a JSON string, number, or boolean" },
        { "no transitions", WithFirstStep(s => s with { Transitions = [] }), "needs at least one transition" },
        { "a missing visit cap", WithFirstStep(s => s with { Transitions = [On("done", "act", 0)] }), "needs a visit cap greater than zero" },
        { "a negative visit cap", WithFirstStep(s => s with { Transitions = [On("done", "act", -1)] }), "needs a visit cap greater than zero" },
        { "a target that is not a step", WithFirstStep(s => s with { Transitions = [On("done", "nowhere")] }), "targets 'nowhere', which is not a step of the flow, park, or end" },
        { "an answer field that is not plain", WithFirstStep(s => s with { Transitions = [new FlowTransition { Field = "a == b", Value = FlowValue.Of("x"), VisitCap = 1, Target = "act" }] }), "an answer field is plain" },
        { "a blank answer field", WithFirstStep(s => s with { Transitions = [new FlowTransition { Value = FlowValue.Of("x"), VisitCap = 1, Target = "act" }] }), "an answer field is plain" },
        { "an object value", WithFirstStep(s => s with { Transitions = [new FlowTransition { Field = "verdict", Value = Json("{}"), VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
        { "a null value", WithFirstStep(s => s with { Transitions = [new FlowTransition { Field = "verdict", Value = Json("null"), VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
        { "a missing value", WithFirstStep(s => s with { Transitions = [new FlowTransition { Field = "verdict", VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
    };

    [Fact]
    public void A_valid_flow_passes_and_park_and_end_are_valid_targets()
    {
        FlowDefinition flow = Valid() with
        {
            Steps = [Judge() with { Transitions = [On("done", "act"), On("stuck", FlowDefinition.Park), On("skip", FlowDefinition.End)] }, Act()],
        };

        FluentActions.Invoking(() => flow.Validate(Known)).Should().NotThrow();
        flow.First!.Name.Should().Be("judge");
        flow.Step("act").Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_flow_that_breaks_a_rule_is_refused_quoting_it(string rule, FlowDefinition flow, string message)
    {
        rule.Should().NotBeNullOrEmpty();
        FluentActions.Invoking(() => flow.Validate(Known))
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
        FlowDefinition.IsPlainName("verdict").Should().BeTrue();
        FlowDefinition.IsPlainName("Not-Clean_2").Should().BeTrue();
        FlowDefinition.IsPlainName("").Should().BeFalse();
        FlowDefinition.IsPlainName(null).Should().BeFalse();
        FlowDefinition.IsPlainName("a b").Should().BeFalse();
        FlowDefinition.IsPlainName("a.b").Should().BeFalse();
        FlowDefinition.IsPlainName("a==b").Should().BeFalse();
    }

    [Fact]
    public void Content_round_trips_to_a_flow_with_the_same_hash()
    {
        FlowDefinition flow = Valid();

        FlowDefinition read = FlowDefinition.FromJson(FlowDefinition.CanonicalJson(flow));

        read.Hash.Should().Be(flow.Hash);
        read.Validate(Known);
    }

    [Theory]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"s","Agent":"a","AnswerSchema":"b","Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"end","Condition":"cost < 5"}]}]}""", "Condition")]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"s","Agent":"a","AnswerSchema":"b","Expression":"x","Transitions":[]}]}""", "Expression")]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Retries":3,"Steps":[]}""", "Retries")]
    public void Content_carrying_a_property_the_shape_does_not_define_is_refused_naming_it(string content, string property)
    {
        FluentActions.Invoking(() => FlowDefinition.FromJson(content))
            .Should().Throw<DomainException>().WithMessage($"The content cannot be read as a flow*{property}*");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public void Content_that_is_not_a_flow_is_refused(string content)
    {
        FluentActions.Invoking(() => FlowDefinition.FromJson(content))
            .Should().Throw<DomainException>().WithMessage("The content*flow*");
    }

    [Fact]
    public void Content_missing_properties_reads_and_is_refused_by_validation_naming_the_rule()
    {
        FlowDefinition read = FlowDefinition.FromJson("{}");

        FluentActions.Invoking(() => read.Validate(Known))
            .Should().Throw<DomainException>().WithMessage("A flow needs a name.");
    }

    [Fact]
    public void Two_flows_built_separately_from_the_same_data_hash_equal()
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
        WithActStep(s => s with { Transitions = [On("done", FlowDefinition.Park), On("failed", "judge")] }).Hash.Should().NotBe(baseline);
        WithActStep(s => s with
        {
            Arguments = new Dictionary<string, JsonElement> { ["state"] = FlowValue.Of("build"), ["count"] = FlowValue.Of(2) },
        }).Hash.Should().NotBe(baseline);
    }

    [Fact]
    public void The_hash_does_not_depend_on_the_order_arguments_were_written_in()
    {
        FlowDefinition reordered = WithActStep(s => s with
        {
            Arguments = new Dictionary<string, JsonElement> { ["count"] = FlowValue.Of(2), ["state"] = FlowValue.Of("review") },
        });

        reordered.Hash.Should().Be(Valid().Hash);
    }

    [Fact]
    public void A_small_fixed_flow_hashes_to_a_literal()
    {
        var flow = new FlowDefinition
        {
            Name = "tiny",
            Accepts = ["task"],
            Outcome = "report-produced",
            Budget = 2m,
            SpawnCap = 1,
            Steps =
            [
                new FlowStep
                {
                    Name = "look",
                    Agent = "reviewer",
                    AnswerSchema = "review",
                    Transitions = [On("clean", FlowDefinition.End, 1), On("not-clean", FlowDefinition.Park, 1)],
                },
            ],
        };

        flow.Hash.Should().Be("b1af6f9835f6e650");
    }

    [Fact]
    public void The_flow_shape_and_its_validation_name_no_software_action()
    {
        string source = File.ReadAllText(SourceFile("Flows", "FlowDefinition.cs"));

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
