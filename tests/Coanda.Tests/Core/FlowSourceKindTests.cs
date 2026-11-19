using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;

namespace Coanda.Tests.Core;

/// <summary>
/// Besides a step of its own flow, an agent step's source can name a step of a named flow, the open
/// parcels of a named type, or the parcel's origin; a flow whose sources name only steps keeps its
/// content and hash.
/// </summary>
public sealed class FlowSourceKindTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string> { "do-thing" };

    private static FlowTransition On(string verdict, string target) =>
        new() { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = 2, Target = target };

    private static FlowSource From(string step) => new() { Step = step };

    private static FlowSource FromFlow(string flow, string step) => new() { Flow = flow, Step = step };

    private static FlowSource OfType(string type) => new() { Type = type };

    private static readonly FlowSource TheOrigin = new() { Origin = true };

    private static Flow With(IReadOnlyList<FlowSource> judgeSources) => new()
    {
        Name = "sample",
        Accepts = ["task"],
        Outcome = "thing-done",
        Budget = 1m,
        SpawnCap = 2,
        Steps =
        [
            new FlowStep { Name = "act", SystemStep = "do-thing", Transitions = [On("done", "judge")] },
            new FlowStep { Name = "judge", Agent = "an-agent", AnswerSchema = "a-schema", Sources = judgeSources, Transitions = [On("done", Flow.End)] },
        ],
    };

    private static readonly FlowSource[] EveryKind = [From("act"), FromFlow("review", "verdict-step"), OfType("decision"), TheOrigin];

    private static DefinitionDrafted Drafted(Flow flow) => DefinitionDecider.Draft(
        DefinitionKind.Flow, flow.Name, DefinitionScope.Project, "alpha", 0,
        Flow.CanonicalJson(flow), flow.Hash, null, "brian", DateTimeOffset.UnixEpoch, knownSystemSteps: Known);

    /// <summary>The flow's content with the judge step's sources replaced by the given JSON array.</summary>
    private static string WithSourcesJson(string sources)
    {
        JsonNode node = JsonNode.Parse(Flow.CanonicalJson(With([From("act")]))) ?? throw new InvalidOperationException("The flow wrote nothing.");
        JsonNode judge = node["Steps"]?[1] ?? throw new InvalidOperationException("The flow has no judge step.");
        judge["Sources"] = JsonNode.Parse(sources);
        return node.ToJsonString();
    }

    private static string Refusal(string sources) =>
        Assert.Throws<DomainException>(() => Flow.FromJson(WithSourcesJson(sources)).Validate(Known)).Message;

    [Fact]
    public void A_step_declaring_one_source_of_each_kind_validates_drafts_and_reads_back_with_the_same_sources_order_and_hash()
    {
        Flow declared = With(EveryKind);

        declared.Validate(Known);
        Assert.Equal(declared.Hash, Drafted(declared).ContentHash);

        string content = Flow.CanonicalJson(declared);
        Flow read = Flow.FromJson(content);
        Assert.Equal(EveryKind, read.Steps[1].Sources);
        Assert.Equal(declared.Hash, read.Hash);
        Assert.Equal(content, Flow.CanonicalJson(read));

        JsonArray sources = JsonNode.Parse(content)?["Steps"]?[1]?["Sources"]?.AsArray() ?? throw new InvalidOperationException("No sources were written.");
        Assert.Equal(
            ["""{"Step":"act"}""", """{"Flow":"review","Step":"verdict-step"}""", """{"Type":"decision"}""", """{"Origin":true}"""],
            sources.Select(source => source?.ToJsonString()));
    }

    [Fact]
    public void Every_source_of_the_shipped_build_flow_holds_the_one_member_step()
    {
        JsonNode node = JsonNode.Parse(Flow.CanonicalJson(SoftwareFlows.Build)) ?? throw new InvalidOperationException("The flow wrote nothing.");
        JsonObject[] sources = [.. (node["Steps"]?.AsArray() ?? []).SelectMany(step => step?["Sources"]?.AsArray() ?? []).Select(source => source?.AsObject() ?? throw new InvalidOperationException("A null source."))];

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.Equal(["Step"], source.Select(member => member.Key)));
    }

    [Fact]
    public void Sources_that_differ_only_in_kind_or_name_hash_differently()
    {
        (Flow, Flow)[] pairs =
        [
            (With([From("act")]), With([FromFlow("sample", "act")])),
            (With([FromFlow("review", "act")]), With([FromFlow("audit", "act")])),
            (With([FromFlow("review", "act")]), With([FromFlow("review", "judge")])),
            (With([OfType("decision")]), With([OfType("lesson")])),
            (With([TheOrigin]), With([])),
        ];

        Assert.All(pairs, pair => Assert.NotEqual(pair.Item1.Hash, pair.Item2.Hash));
    }

    [Fact]
    public void A_named_flows_source_may_name_its_own_flow_and_the_declaring_step_beside_a_step_source_of_the_same_step()
    {
        Flow flow = With([From("act"), FromFlow("sample", "act"), FromFlow("sample", "judge"), FromFlow("sample", "nowhere")]);

        flow.Validate(Known);
        Assert.Equal(flow.Hash, Drafted(flow).ContentHash);
    }

    [Fact]
    public void A_flow_or_type_no_definition_defines_is_still_accepted_when_drafted()
    {
        Flow flow = With([FromFlow("no-such-flow", "no-such-step"), OfType("no-such-type")]);

        Assert.Equal(flow.Hash, Drafted(flow).ContentHash);
    }

    private const string NoKind =
        "which names no kind; a source names a step of the flow (Step), a step of a named flow (Flow and Step), the open parcels of a type (Type), or the parcel's origin (Origin true).";

    private const string MoreThanOne = "which names more than one kind; a source names one of a step, a flow and its step, a type, or the origin.";

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Step":null}""")]
    [InlineData("""{"Step":""}""")]
    [InlineData("""{"Step":" "}""")]
    [InlineData("""{"Origin":false}""")]
    [InlineData("""{"Type":null}""")]
    public void A_source_naming_no_kind_is_refused_naming_the_flow_the_step_and_its_position(string source)
    {
        Assert.Equal($"Agent step 'judge' of flow 'sample' declares source 2, {NoKind}", Refusal($"[{{\"Step\":\"act\"}},{source}]"));
    }

    [Theory]
    [InlineData("""{"Type":"t","Flow":"f"}""")]
    [InlineData("""{"Type":"t","Step":"act"}""")]
    [InlineData("""{"Type":"t","Origin":true}""")]
    [InlineData("""{"Type":"t","Flow":"f","Step":"s"}""")]
    [InlineData("""{"Origin":true,"Flow":"f"}""")]
    [InlineData("""{"Origin":true,"Step":"act"}""")]
    [InlineData("""{"Origin":true,"Flow":"f","Step":"s"}""")]
    public void A_source_naming_more_than_one_kind_is_refused_naming_the_flow_the_step_and_its_position(string source)
    {
        Assert.Equal($"Agent step 'judge' of flow 'sample' declares source 2, {MoreThanOne}", Refusal($"[{{\"Step\":\"act\"}},{source}]"));
    }

    [Theory]
    [InlineData("""{"Flow":"review"}""")]
    [InlineData("""{"Flow":"review","Step":null}""")]
    public void A_source_naming_a_flow_and_no_step_is_refused_naming_the_flow_the_step_and_its_position(string source)
    {
        Assert.Equal(
            "Agent step 'judge' of flow 'sample' declares source 1, which names the flow 'review' and no step; a source naming a flow names the step of it whose latest recording on the parcel its context carries.",
            Refusal($"[{source}]"));
    }

    [Theory]
    [InlineData("""{"Flow":"","Step":"act"}""", "flow", "")]
    [InlineData("""{"Flow":"re view","Step":"act"}""", "flow", "re view")]
    [InlineData("""{"Flow":"review","Step":""}""", "step", "")]
    [InlineData("""{"Flow":"review","Step":"a.b"}""", "step", "a.b")]
    [InlineData("""{"Type":""}""", "type", "")]
    [InlineData("""{"Type":"de/cision"}""", "type", "de/cision")]
    public void A_name_that_is_not_plain_is_refused_naming_the_flow_the_step_and_its_position(string source, string what, string name)
    {
        Assert.Equal(
            $"Agent step 'judge' of flow 'sample' declares source 2 naming the {what} '{name}'; a {what}'s name in a source is plain: letters, digits, hyphens, and underscores, and not empty.",
            Refusal($"[{{\"Step\":\"act\"}},{source}]"));
    }

    [Theory]
    [InlineData("""{"Flow":"review","Step":"act"}""")]
    [InlineData("""{"Type":"decision"}""")]
    [InlineData("""{"Origin":true}""")]
    public void A_source_of_a_new_kind_declared_twice_is_refused_naming_the_flow_the_step_and_the_second_position(string source)
    {
        Assert.Equal(
            "Agent step 'judge' of flow 'sample' declares source 3, which is the same as an earlier source; a step declares each source once.",
            Refusal($"[{source},{{\"Step\":\"act\"}},{source}]"));
    }

    [Fact]
    public void New_kind_sources_are_the_same_only_when_their_names_match_ordinally()
    {
        With([FromFlow("review", "act"), FromFlow("Review", "act"), FromFlow("review", "Act"), OfType("decision"), OfType("Decision")]).Validate(Known);
    }

    [Theory]
    [InlineData("""{"Flow":"review","Step":"act"}""")]
    [InlineData("""{"Type":"decision"}""")]
    [InlineData("""{"Origin":true}""")]
    public void A_system_step_declaring_a_source_of_a_new_kind_is_refused_as_before(string source)
    {
        JsonNode node = JsonNode.Parse(Flow.CanonicalJson(With([]))) ?? throw new InvalidOperationException("The flow wrote nothing.");
        JsonNode act = node["Steps"]?[0] ?? throw new InvalidOperationException("The flow has no act step.");
        act["Sources"] = JsonNode.Parse($"[{source}]");

        DomainException exception = Assert.Throws<DomainException>(() => Flow.FromJson(node.ToJsonString()).Validate(Known));
        Assert.Equal("System step 'act' of flow 'sample' declares sources; only an agent step declares the sources its context carries.", exception.Message);
    }
}
