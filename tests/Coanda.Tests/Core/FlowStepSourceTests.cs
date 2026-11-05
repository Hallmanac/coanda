using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Definitions;

namespace Coanda.Tests.Core;

/// <summary>An agent step declares the steps of its flow whose latest recordings its context carries; a system step declares none.</summary>
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
            new FlowStep { Name = "act", SystemStep = "do-thing", Sources = actSources ?? [], Transitions = [On("done", "judge")] },
            new FlowStep { Name = "judge", Agent = "an-agent", AnswerSchema = "a-schema", Sources = judgeSources, Transitions = [On("done", "check")] },
            new FlowStep { Name = "check", Agent = "an-agent", AnswerSchema = "a-schema", Transitions = [On("done", Flow.End)] },
        ],
    };

    [Fact]
    public void An_agent_step_declaring_distinct_steps_of_its_flow_loads()
    {
        With([From("check"), From("act")]).Validate(Known);
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
        Assert.DoesNotContain("Sources", undeclared, StringComparison.Ordinal);

        empty.Validate(Known);
        Assert.Equal(empty.Hash, Flow.FromJson(undeclared).Hash);
        Assert.Equal(empty.Hash, Flow.FromJson(content).Hash);
    }

    [Fact]
    public void A_source_is_written_as_an_object_naming_its_step_in_the_flows_casing()
    {
        string content = Flow.CanonicalJson(With([From("act")]));

        Assert.Contains("\"Sources\": [", content, StringComparison.Ordinal);
        Assert.Contains("\"Step\": \"act\"", content, StringComparison.Ordinal);
        Assert.Equal([From("a")], Flow.FromJson("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"a","SystemStep":"do-thing","Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"j"}]},{"Name":"j","Agent":"x","AnswerSchema":"y","Sources":[{"Step":"a"}],"Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"end"}]}]}""")
            .Steps[1].Sources);
    }

    [Theory]
    [InlineData("""{"Journal":true}""", "Journal")]
    [InlineData("""{"Step":"act","Notes":"all"}""", "Notes")]
    [InlineData("""{"step":"act"}""", "step")]
    public void A_source_holding_a_property_no_kind_defines_is_refused_when_read_naming_it(string source, string property)
    {
        string content = Flow.CanonicalJson(With([From("act")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"act\"\n        }", source, StringComparison.Ordinal);
        // the test replaced the declared source
        Assert.Contains(source, content, StringComparison.Ordinal);

        DomainException exception = Assert.Throws<DomainException>(() => Flow.FromJson(content));
        Assert.Matches(WildcardPattern.ToRegex($"The content cannot be read as a flow*{property}*"), exception.Message);
    }

    [Fact]
    public void A_system_step_declaring_a_source_is_refused_quoting_the_rule()
    {
        DomainException exception = Assert.Throws<DomainException>(() => With([], actSources: [From("check")]).Validate(Known));
        Assert.Equal("System step 'act' of flow 'sample' declares sources; only an agent step declares the sources its context carries.", exception.Message);
    }

    [Fact]
    public void A_null_source_is_refused_naming_its_position()
    {
        string content = Flow.CanonicalJson(With([From("act"), From("check")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"check\"\n        }", "null", StringComparison.Ordinal);

        DomainException exception = Assert.Throws<DomainException>(() => Flow.FromJson(content).Validate(Known));
        Assert.Equal("Agent step 'judge' of flow 'sample' declares source 2 as null; each source is an object naming the step it reads.", exception.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Step":null}""")]
    [InlineData("""{"Step":""}""")]
    [InlineData("""{"Step":"  "}""")]
    public void A_source_naming_no_step_is_refused_naming_its_position(string source)
    {
        string content = Flow.CanonicalJson(With([From("act"), From("check")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"check\"\n        }", source, StringComparison.Ordinal);
        // the test replaced the second source
        Assert.Contains(source, content, StringComparison.Ordinal);

        DomainException exception = Assert.Throws<DomainException>(() => Flow.FromJson(content).Validate(Known));
        Assert.Equal("Agent step 'judge' of flow 'sample' declares source 2, which names no step; a source names the step of the flow whose latest recording its context carries.", exception.Message);
    }

    [Fact]
    public void A_source_naming_no_step_of_the_flow_is_refused_naming_it()
    {
        DomainException exception = Assert.Throws<DomainException>(() => With([From("act"), From("elsewhere")]).Validate(Known));
        Assert.Equal("Agent step 'judge' of flow 'sample' declares the source step 'elsewhere', which is not a step of the flow.", exception.Message);
        DomainException exception2 = Assert.Throws<DomainException>(() => With([From("Act")]).Validate(Known));
        // a step is named as the flow names it
        Assert.EndsWith("the source step 'Act', which is not a step of the flow.", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_naming_the_declaring_step_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => With([From("act"), From("judge")]).Validate(Known));
        Assert.Equal("Agent step 'judge' of flow 'sample' declares itself as a source; a step's own earlier visits are not a source.", exception.Message);
    }

    [Fact]
    public void A_source_naming_the_same_step_as_an_earlier_one_is_refused_naming_it()
    {
        DomainException exception = Assert.Throws<DomainException>(() => With([From("act"), From("check"), From("act")]).Validate(Known));
        Assert.Equal("Agent step 'judge' of flow 'sample' declares the source step 'act' twice; a step declares each source once.", exception.Message);
    }

    [Fact]
    public void A_flow_drafted_through_the_definitions_path_is_refused_by_the_same_rules()
    {
        Flow broken = With([From("judge")]);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.Flow, broken.Name, DefinitionScope.Project, "alpha", 0,
                Flow.CanonicalJson(broken), broken.Hash, null, "brian", DateTimeOffset.UnixEpoch, knownSystemSteps: Known));
        Assert.StartsWith("Agent step 'judge' of flow 'sample' declares itself as a source", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_sources_survive_the_canonical_json_and_are_part_of_the_hash()
    {
        Flow declared = With([From("act"), From("check")]);

        Flow read = Flow.FromJson(Flow.CanonicalJson(declared));

        Assert.Equal([From("act"), From("check")], read.Steps[1].Sources);
        Assert.Equal(declared.Hash, read.Hash);
        string[] hashes = [With([]).Hash, declared.Hash, With([From("check"), From("act")]).Hash, With([From("act")]).Hash];
        // the sources and their order are part of the hash
        Assert.Distinct(hashes);
    }

    [Fact]
    public void The_shipped_build_flows_fix_step_reads_the_gates_and_both_reviews_and_no_other_shipped_step_declares_a_source()
    {
        Assert.Equal(["gates", "adversarial-review", "conformance-review"], SoftwareFlows.Build.Step("fix")?.Sources.Select(s => s.Step));

        Assert.All(SoftwareFlows.All.SelectMany(c => c.Steps.Where(s => !(c == SoftwareFlows.Build && s.Name == "fix"))), s => Assert.True(s.Sources.Count == 0));
        SoftwareFlows.Build.Validate(ShippedSystemSteps.Names);
    }
}
