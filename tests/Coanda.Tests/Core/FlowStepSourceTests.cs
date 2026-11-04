using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Definitions;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

/// <summary>A judgment step declares the steps of its flow whose latest recordings its context carries; an action step declares none.</summary>
public sealed class FlowStepSourceTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string> { "do-thing" };

    private static FlowTransition On(string verdict, string target) =>
        new() { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = 2, Target = target };

    private static FlowSource From(string? step) => new() { Step = step };

    private static Flow With(IReadOnlyList<FlowSource> judgeSources, IReadOnlyList<FlowSource>? actSources = null) => new()
    {
        Name = "sample",
        Accepts = ["task"],
        Outcome = "thing-done",
        Budget = 1m,
        SpawnCap = 2,
        Steps =
        [
            new FlowStep { Name = "act", Action = "do-thing", Sources = actSources ?? [], Transitions = [On("done", "judge")] },
            new FlowStep { Name = "judge", Agent = "an-agent", AnswerSchema = "a-schema", Sources = judgeSources, Transitions = [On("done", "check")] },
            new FlowStep { Name = "check", Agent = "an-agent", AnswerSchema = "a-schema", Transitions = [On("done", Flow.End)] },
        ],
    };

    [Fact]
    public void A_judgment_declaring_distinct_steps_of_its_flow_loads()
    {
        FluentActions.Invoking(() => With([From("check"), From("act")]).Validate(Known)).Should().NotThrow();
    }

    [Fact]
    public void An_empty_list_is_the_same_as_declaring_none()
    {
        Flow empty = With([]);
        string content = Flow.CanonicalJson(empty);
        JsonNode node = JsonNode.Parse(content) ?? throw new InvalidOperationException("The flow wrote nothing.");
        foreach (JsonNode? step in node["Steps"]?.AsArray() ?? [])
        {
            step?.AsObject().Remove("Sources");
        }

        string undeclared = node.ToJsonString();
        undeclared.Should().NotContain("Sources");

        empty.Validate(Known);
        Flow.FromJson(undeclared).Hash.Should().Be(empty.Hash);
        Flow.FromJson(content).Hash.Should().Be(empty.Hash);
    }

    [Fact]
    public void A_source_is_written_as_an_object_naming_its_step_in_the_flows_casing()
    {
        string content = Flow.CanonicalJson(With([From("act")]));

        content.Should().Contain("\"Sources\": [").And.Contain("\"Step\": \"act\"");
        Flow.FromJson("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"a","Action":"do-thing","Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"j"}]},{"Name":"j","Agent":"x","AnswerSchema":"y","Sources":[{"Step":"a"}],"Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"end"}]}]}""")
            .Steps[1].Sources.Should().Equal(From("a"));
    }

    [Theory]
    [InlineData("""{"Journal":true}""", "Journal")]
    [InlineData("""{"Step":"act","Notes":"all"}""", "Notes")]
    [InlineData("""{"step":"act"}""", "step")]
    public void A_source_holding_a_property_no_kind_defines_is_refused_when_read_naming_it(string source, string property)
    {
        string content = Flow.CanonicalJson(With([From("act")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"act\"\n        }", source, StringComparison.Ordinal);
        content.Should().Contain(source, "the test replaced the declared source");

        FluentActions.Invoking(() => Flow.FromJson(content))
            .Should().Throw<DomainException>().WithMessage($"The content cannot be read as a flow*{property}*");
    }

    [Fact]
    public void An_action_step_declaring_a_source_is_refused_quoting_the_rule()
    {
        FluentActions.Invoking(() => With([], actSources: [From("check")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Action step 'act' of flow 'sample' declares sources; only a judgment step declares the sources its context carries.");
    }

    [Fact]
    public void A_null_source_is_refused_naming_its_position()
    {
        string content = Flow.CanonicalJson(With([From("act"), From("check")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"check\"\n        }", "null", StringComparison.Ordinal);

        FluentActions.Invoking(() => Flow.FromJson(content).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of flow 'sample' declares source 2 as null; each source is an object naming the step it reads.");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Step":null}""")]
    [InlineData("""{"Step":""}""")]
    [InlineData("""{"Step":"  "}""")]
    public void A_source_naming_no_step_is_refused_naming_its_position(string source)
    {
        string content = Flow.CanonicalJson(With([From("act"), From("check")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"check\"\n        }", source, StringComparison.Ordinal);
        content.Should().Contain(source, "the test replaced the second source");

        FluentActions.Invoking(() => Flow.FromJson(content).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of flow 'sample' declares source 2, which names no step; a source names the step of the flow whose latest recording its context carries.");
    }

    [Fact]
    public void A_source_naming_no_step_of_the_flow_is_refused_naming_it()
    {
        FluentActions.Invoking(() => With([From("act"), From("elsewhere")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of flow 'sample' declares the source step 'elsewhere', which is not a step of the flow.");
        FluentActions.Invoking(() => With([From("Act")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("*the source step 'Act', which is not a step of the flow.", "a step is named as the flow names it");
    }

    [Fact]
    public void A_source_naming_the_declaring_step_is_refused()
    {
        FluentActions.Invoking(() => With([From("act"), From("judge")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of flow 'sample' declares itself as a source; a step's own earlier visits are not a source.");
    }

    [Fact]
    public void A_source_naming_the_same_step_as_an_earlier_one_is_refused_naming_it()
    {
        FluentActions.Invoking(() => With([From("act"), From("check"), From("act")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of flow 'sample' declares the source step 'act' twice; a step declares each source once.");
    }

    [Fact]
    public void A_flow_drafted_through_the_definitions_path_is_refused_by_the_same_rules()
    {
        Flow broken = With([From("judge")]);

        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.Flow, broken.Name, DefinitionScope.Project, "alpha", 0,
                Flow.CanonicalJson(broken), broken.Hash, null, "brian", DateTimeOffset.UnixEpoch, knownActions: Known))
            .Should().Throw<DomainException>().WithMessage("Judgment step 'judge' of flow 'sample' declares itself as a source*");
    }

    [Fact]
    public void Declared_sources_survive_the_canonical_json_and_are_part_of_the_hash()
    {
        Flow declared = With([From("act"), From("check")]);

        Flow read = Flow.FromJson(Flow.CanonicalJson(declared));

        read.Steps[1].Sources.Should().Equal(From("act"), From("check"));
        read.Hash.Should().Be(declared.Hash);
        string[] hashes = [With([]).Hash, declared.Hash, With([From("check"), From("act")]).Hash, With([From("act")]).Hash];
        hashes.Should().OnlyHaveUniqueItems("the sources and their order are part of the hash");
    }

    [Fact]
    public void The_shipped_build_flows_fix_step_reads_the_gates_and_both_reviews_and_no_other_shipped_step_declares_a_source()
    {
        ShippedFlows.Build.Step("fix")?.Sources.Select(s => s.Step).Should().Equal("gates", "adversarial-review", "conformance-review");

        ShippedFlows.All.SelectMany(c => c.Steps.Where(s => !(c == ShippedFlows.Build && s.Name == "fix")))
            .Should().OnlyContain(s => s.Sources.Count == 0);
        ShippedFlows.Build.Validate(ShippedActions.Names);
    }
}
