using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;

namespace Coanda.Tests.Core;

public sealed class FlowTests
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
        SystemStep = "do-thing",
        Arguments = new Dictionary<string, JsonElement> { ["stage"] = FlowValue.Of("review"), ["count"] = FlowValue.Of(2) },
        Transitions = [On("done", Flow.End), On("failed", "judge")],
    };

    private static Flow Valid() => new()
    {
        Name = "sample",
        Accepts = ["task"],
        Outcome = "thing-done",
        Budget = 4.5m,
        SpawnCap = 6,
        Steps = [Judge(), Act()],
    };

    private static Flow WithFirstStep(Func<FlowStep, FlowStep> change) =>
        Valid() with { Steps = [change(Judge()), Act()] };

    private static Flow WithActStep(Func<FlowStep, FlowStep> change) =>
        Valid() with { Steps = [Judge(), change(Act())] };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public static TheoryData<string, Flow, string> Refusals => new()
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
        { "a step named pause", Valid() with { Steps = [Judge("pause"), Act()] }, "has a step named 'pause'" },
        { "a step named end", Valid() with { Steps = [Judge(), Act("end")] }, "has a step named 'end'" },
        { "an agent and a system step", WithFirstStep(s => s with { SystemStep = "do-thing" }), "must name an agent (an agent step) or a system step, and exactly one of them" },
        { "neither agent nor system step", WithFirstStep(s => s with { Agent = null }), "must name an agent (an agent step) or a system step, and exactly one of them" },
        { "an agent step without a schema", WithFirstStep(s => s with { AnswerSchema = null }), "needs an agent and an answer schema" },
        { "an agent step with a blank schema", WithFirstStep(s => s with { AnswerSchema = " " }), "needs an agent and an answer schema" },
        { "an unknown system step", WithActStep(s => s with { SystemStep = "worktree" }), "names the system step 'worktree', which is not a known system step" },
        { "an argument with a spaced name", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a name"] = FlowValue.Of("x") } }), "an argument name is plain" },
        { "an argument with a dotted name", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a.b"] = FlowValue.Of("x") } }), "an argument name is plain" },
        { "an object argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("""{"x":1}""") } }), "Argument 'a' of step 'act' in flow 'sample' must be a JSON string, number, or boolean." },
        { "an array argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("[1]") } }), "must be a JSON string, number, or boolean" },
        { "a null argument", WithActStep(s => s with { Arguments = new Dictionary<string, JsonElement> { ["a"] = Json("null") } }), "must be a JSON string, number, or boolean" },
        { "no transitions", WithFirstStep(s => s with { Transitions = [] }), "needs at least one transition" },
        { "a missing visit cap", WithFirstStep(s => s with { Transitions = [On("done", "act", 0)] }), "needs a visit cap greater than zero" },
        { "a negative visit cap", WithFirstStep(s => s with { Transitions = [On("done", "act", -1)] }), "needs a visit cap greater than zero" },
        { "a target that is not a step", WithFirstStep(s => s with { Transitions = [On("done", "nowhere")] }), "targets 'nowhere', which is not a step of the flow, pause, or end" },
        { "an answer field that is not plain", WithFirstStep(s => s with { Transitions = [new FlowTransition { Field = "a == b", Value = FlowValue.Of("x"), VisitCap = 1, Target = "act" }] }), "an answer field is plain" },
        { "a blank answer field", WithFirstStep(s => s with { Transitions = [new FlowTransition { Value = FlowValue.Of("x"), VisitCap = 1, Target = "act" }] }), "an answer field is plain" },
        { "an object value", WithFirstStep(s => s with { Transitions = [new FlowTransition { Field = "verdict", Value = Json("{}"), VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
        { "a null value", WithFirstStep(s => s with { Transitions = [new FlowTransition { Field = "verdict", Value = Json("null"), VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
        { "a follow with a blank outcome", Valid() with { Follow = new FlowFollow { Outcome = " ", DepthCap = 2 } }, "its follow needs the outcome's name" },
        { "a follow with no outcome", Valid() with { Follow = new FlowFollow { DepthCap = 2 } }, "its follow needs the outcome's name" },
        { "a follow naming a flow that is not plain", Valid() with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build or fix", DepthCap = 2 } }, "a followed flow's name is plain" },
        { "a follow naming a blank flow", Valid() with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "", DepthCap = 2 } }, "a followed flow's name is plain" },
        { "a follow with no depth cap", Valid() with { Follow = new FlowFollow { Outcome = "pr-opened" } }, "its follow needs a depth cap greater than zero" },
        { "a follow with a negative depth cap", Valid() with { Follow = new FlowFollow { Outcome = "pr-opened", DepthCap = -1 } }, "its follow needs a depth cap greater than zero" },
        { "an empty closing resolution", Valid() with { Closes = "" }, "a closing resolution is plain: letters, digits, hyphens, and underscores, and not empty" },
        { "a blank closing resolution", Valid() with { Closes = " " }, "closes its parcel with the resolution ' '; a closing resolution is plain" },
        { "a closing resolution that is not plain", Valid() with { Closes = "completed or not" }, "closes its parcel with the resolution 'completed or not'; a closing resolution is plain" },
        { "a missing value", WithFirstStep(s => s with { Transitions = [new FlowTransition { Field = "verdict", VisitCap = 1, Target = "act" }] }), "needs a value that is a JSON string, number, or boolean" },
    };

    [Fact]
    public void A_valid_flow_passes_and_pause_and_end_are_valid_targets()
    {
        Flow flow = Valid() with
        {
            Steps = [Judge() with { Transitions = [On("done", "act"), On("stuck", Flow.Pause), On("skip", Flow.End)] }, Act()],
        };

        flow.Validate(Known);
        Assert.Equal("judge", flow.First!.Name);
        Assert.NotNull(flow.Step("act"));
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_flow_that_breaks_a_rule_is_refused_quoting_it(string rule, Flow flow, string message)
    {
        Assert.False(string.IsNullOrEmpty(rule));
        DomainException exception = Assert.Throws<DomainException>(() => flow.Validate(Known));
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_takes_the_known_system_steps_as_an_input()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Valid().Validate(new HashSet<string> { "other" }));
        Assert.Contains("names the system step 'do-thing'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_names_are_letters_digits_hyphens_and_underscores()
    {
        Assert.True(Flow.IsPlainName("verdict"));
        Assert.True(Flow.IsPlainName("Not-Clean_2"));
        Assert.False(Flow.IsPlainName(""));
        Assert.False(Flow.IsPlainName(null));
        Assert.False(Flow.IsPlainName("a b"));
        Assert.False(Flow.IsPlainName("a.b"));
        Assert.False(Flow.IsPlainName("a==b"));
    }

    [Fact]
    public void Content_round_trips_to_a_flow_with_the_same_hash()
    {
        Flow flow = Valid();

        Flow read = Flow.FromJson(Flow.CanonicalJson(flow));

        Assert.Equal(flow.Hash, read.Hash);
        read.Validate(Known);
    }

    [Theory]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"s","Agent":"a","AnswerSchema":"b","Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"end","Condition":"cost < 5"}]}]}""", "Condition")]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"s","Agent":"a","AnswerSchema":"b","Expression":"x","Transitions":[]}]}""", "Expression")]
    [InlineData("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Retries":3,"Steps":[]}""", "Retries")]
    public void Content_carrying_a_property_the_shape_does_not_define_is_refused_naming_it(string content, string property)
    {
        DomainException exception = Assert.Throws<DomainException>(() => Flow.FromJson(content));
        Assert.Matches(WildcardPattern.ToRegex($"The content cannot be read as a flow*{property}*"), exception.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public void Content_that_is_not_a_flow_is_refused(string content)
    {
        DomainException exception = Assert.Throws<DomainException>(() => Flow.FromJson(content));
        Assert.Matches(@"(?s)^The content.*flow.*$", exception.Message);
    }

    [Fact]
    public void Content_holding_a_null_step_is_refused_naming_the_flow_and_the_rule()
    {
        Flow read = Flow.FromJson(
            """{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[null]}""");

        DomainException exception = Assert.Throws<DomainException>(() => read.Validate(Known));
        Assert.StartsWith("Flow 'sample' has a step that is null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_holding_a_null_transition_is_refused_naming_the_flow_and_the_rule()
    {
        Flow read = Flow.FromJson(
            """{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"s","Agent":"a","AnswerSchema":"b","Transitions":[null]}]}""");

        DomainException exception = Assert.Throws<DomainException>(() => read.Validate(Known));
        Assert.StartsWith("Step 's' of flow 'sample' has a transition that is null", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_missing_properties_reads_and_is_refused_by_validation_naming_the_rule()
    {
        Flow read = Flow.FromJson("{}");

        DomainException exception = Assert.Throws<DomainException>(() => read.Validate(Known));
        Assert.Equal("A flow needs a name.", exception.Message);
    }

    [Fact]
    public void Two_flows_built_separately_from_the_same_data_hash_equal()
    {
        Assert.Equal(Valid().Hash, Valid().Hash);
        Assert.Equal(16, Valid().Hash.Length);
    }

    [Fact]
    public void Changing_the_budget_a_visit_cap_a_target_or_an_argument_changes_the_hash()
    {
        string baseline = Valid().Hash;

        Assert.NotEqual(baseline, (Valid() with { Budget = 5m }).Hash);
        Assert.NotEqual(baseline, (Valid() with { SpawnCap = 7 }).Hash);
        Assert.NotEqual(baseline, WithFirstStep(s => s with { Transitions = [On("done", "act", 3)] }).Hash);
        Assert.NotEqual(baseline, WithActStep(s => s with { Transitions = [On("done", Flow.Pause), On("failed", "judge")] }).Hash);
        Assert.NotEqual(baseline, WithActStep(s => s with
        {
            Arguments = new Dictionary<string, JsonElement> { ["stage"] = FlowValue.Of("build"), ["count"] = FlowValue.Of(2) },
        }).Hash);
    }

    [Fact]
    public void A_valid_follow_passes_and_round_trips_with_its_hash()
    {
        Flow follower = Valid() with { Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 2 } };

        follower.Validate(Known);
        (Valid() with { Follow = new FlowFollow { Outcome = "pr-opened", DepthCap = 1 } }).Validate(Known);
        Flow read = Flow.FromJson(Flow.CanonicalJson(follower));
        Assert.Equal(follower.Follow, read.Follow);
        Assert.Equal(follower.Hash, read.Hash);
    }

    [Fact]
    public void Adding_a_follow_or_changing_its_depth_cap_changes_the_hash()
    {
        string baseline = Valid().Hash;
        var follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 2 };
        Flow follower = Valid() with { Follow = follow };

        Assert.NotEqual(baseline, follower.Hash);
        Assert.NotEqual(follower.Hash, (Valid() with { Follow = follow with { DepthCap = 3 } }).Hash);
        Assert.NotEqual(follower.Hash, (Valid() with { Follow = follow with { Flow = null } }).Hash);
        Assert.NotEqual(follower.Hash, (Valid() with { Follow = follow with { Outcome = "report-produced" } }).Hash);
    }

    [Fact]
    public void A_closing_resolution_passes_round_trips_and_changes_the_hash()
    {
        Flow closing = Valid() with { Closes = "completed" };

        closing.Validate(Known);
        Flow read = Flow.FromJson(Flow.CanonicalJson(closing));
        Assert.Equal("completed", read.Closes);
        Assert.Equal(closing.Hash, read.Hash);
        Assert.NotEqual(Valid().Hash, closing.Hash);
        Assert.NotEqual(closing.Hash, (Valid() with { Closes = "abandoned" }).Hash);
    }

    [Fact]
    public void A_flow_without_a_closing_resolution_writes_none_into_its_content_and_reads_back_as_before()
    {
        string content = Flow.CanonicalJson(Valid());

        Assert.DoesNotContain("Closes", content, StringComparison.Ordinal);
        Flow read = Flow.FromJson(content);
        Assert.Null(read.Closes);
        Assert.Equal(Valid().Hash, read.Hash);
        Assert.Equal(content, Flow.CanonicalJson(read));
    }

    [Fact]
    public void A_flow_without_a_follow_writes_no_follow_into_its_content()
    {
        Assert.DoesNotContain("Follow", Flow.CanonicalJson(Valid()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_hash_does_not_depend_on_the_order_arguments_were_written_in()
    {
        Flow reordered = WithActStep(s => s with
        {
            Arguments = new Dictionary<string, JsonElement> { ["count"] = FlowValue.Of(2), ["stage"] = FlowValue.Of("review") },
        });

        Assert.Equal(Valid().Hash, reordered.Hash);
    }

    [Fact]
    public void A_small_fixed_flow_hashes_to_a_literal()
    {
        var flow = new Flow
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
                    Transitions = [On("clean", Flow.End, 1), On("not-clean", Flow.Pause, 1)],
                },
            ],
        };

        Assert.Equal("c7d6781a3fc6c3ff", flow.Hash);
    }

    [Fact]
    public void The_flow_shape_and_its_validation_name_no_software_system_step()
    {
        string source = File.ReadAllText(SourceFile("Flows", "Flow.cs"));

        foreach (string software in new[] { "worktree", "gates", "open-pr", "gated-merge", "rebase", "SoftwareSystemSteps" })
        {
            Assert.DoesNotContain(software, source, StringComparison.Ordinal);
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
