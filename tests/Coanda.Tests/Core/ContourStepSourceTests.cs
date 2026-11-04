using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Definitions;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

/// <summary>A judgment step declares the steps of its contour whose latest recordings its context carries; an action step declares none.</summary>
public sealed class ContourStepSourceTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string> { "do-thing" };

    private static ContourTransition On(string verdict, string target) =>
        new() { Field = "verdict", Value = ContourValue.Of(verdict), VisitCap = 2, Target = target };

    private static ContourSource From(string? step) => new() { Step = step };

    private static Contour With(IReadOnlyList<ContourSource> judgeSources, IReadOnlyList<ContourSource>? actSources = null) => new()
    {
        Name = "sample",
        Accepts = ["task"],
        Outcome = "thing-done",
        Budget = 1m,
        SpawnCap = 2,
        Steps =
        [
            new ContourStep { Name = "act", Action = "do-thing", Sources = actSources ?? [], Transitions = [On("done", "judge")] },
            new ContourStep { Name = "judge", Agent = "an-agent", AnswerSchema = "a-schema", Sources = judgeSources, Transitions = [On("done", "check")] },
            new ContourStep { Name = "check", Agent = "an-agent", AnswerSchema = "a-schema", Transitions = [On("done", Contour.End)] },
        ],
    };

    [Fact]
    public void A_judgment_declaring_distinct_steps_of_its_contour_loads()
    {
        FluentActions.Invoking(() => With([From("check"), From("act")]).Validate(Known)).Should().NotThrow();
    }

    [Fact]
    public void An_empty_list_is_the_same_as_declaring_none()
    {
        Contour empty = With([]);
        string content = Contour.CanonicalJson(empty);
        JsonNode node = JsonNode.Parse(content) ?? throw new InvalidOperationException("The contour wrote nothing.");
        foreach (JsonNode? step in node["Steps"]?.AsArray() ?? [])
        {
            step?.AsObject().Remove("Sources");
        }

        string undeclared = node.ToJsonString();
        undeclared.Should().NotContain("Sources");

        empty.Validate(Known);
        Contour.FromJson(undeclared).Hash.Should().Be(empty.Hash);
        Contour.FromJson(content).Hash.Should().Be(empty.Hash);
    }

    [Fact]
    public void A_source_is_written_as_an_object_naming_its_step_in_the_contours_casing()
    {
        string content = Contour.CanonicalJson(With([From("act")]));

        content.Should().Contain("\"Sources\": [").And.Contain("\"Step\": \"act\"");
        Contour.FromJson("""{"Name":"sample","Accepts":["task"],"Outcome":"o","Budget":1,"SpawnCap":1,"Steps":[{"Name":"a","Action":"do-thing","Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"j"}]},{"Name":"j","Agent":"x","AnswerSchema":"y","Sources":[{"Step":"a"}],"Transitions":[{"Field":"verdict","Value":"done","VisitCap":1,"Target":"end"}]}]}""")
            .Steps[1].Sources.Should().Equal(From("a"));
    }

    [Theory]
    [InlineData("""{"Journal":true}""", "Journal")]
    [InlineData("""{"Step":"act","Notes":"all"}""", "Notes")]
    [InlineData("""{"step":"act"}""", "step")]
    public void A_source_holding_a_property_no_kind_defines_is_refused_when_read_naming_it(string source, string property)
    {
        string content = Contour.CanonicalJson(With([From("act")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"act\"\n        }", source, StringComparison.Ordinal);
        content.Should().Contain(source, "the test replaced the declared source");

        FluentActions.Invoking(() => Contour.FromJson(content))
            .Should().Throw<DomainException>().WithMessage($"The content cannot be read as a contour*{property}*");
    }

    [Fact]
    public void An_action_step_declaring_a_source_is_refused_quoting_the_rule()
    {
        FluentActions.Invoking(() => With([], actSources: [From("check")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Action step 'act' of contour 'sample' declares sources; only a judgment step declares the sources its context carries.");
    }

    [Fact]
    public void A_null_source_is_refused_naming_its_position()
    {
        string content = Contour.CanonicalJson(With([From("act"), From("check")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"check\"\n        }", "null", StringComparison.Ordinal);

        FluentActions.Invoking(() => Contour.FromJson(content).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of contour 'sample' declares source 2 as null; each source is an object naming the step it reads.");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Step":null}""")]
    [InlineData("""{"Step":""}""")]
    [InlineData("""{"Step":"  "}""")]
    public void A_source_naming_no_step_is_refused_naming_its_position(string source)
    {
        string content = Contour.CanonicalJson(With([From("act"), From("check")])).ReplaceLineEndings("\n").Replace("{\n          \"Step\": \"check\"\n        }", source, StringComparison.Ordinal);
        content.Should().Contain(source, "the test replaced the second source");

        FluentActions.Invoking(() => Contour.FromJson(content).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of contour 'sample' declares source 2, which names no step; a source names the step of the contour whose latest recording its context carries.");
    }

    [Fact]
    public void A_source_naming_no_step_of_the_contour_is_refused_naming_it()
    {
        FluentActions.Invoking(() => With([From("act"), From("elsewhere")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of contour 'sample' declares the source step 'elsewhere', which is not a step of the contour.");
        FluentActions.Invoking(() => With([From("Act")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("*the source step 'Act', which is not a step of the contour.", "a step is named as the contour names it");
    }

    [Fact]
    public void A_source_naming_the_declaring_step_is_refused()
    {
        FluentActions.Invoking(() => With([From("act"), From("judge")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of contour 'sample' declares itself as a source; a step's own earlier visits are not a source.");
    }

    [Fact]
    public void A_source_naming_the_same_step_as_an_earlier_one_is_refused_naming_it()
    {
        FluentActions.Invoking(() => With([From("act"), From("check"), From("act")]).Validate(Known)).Should().Throw<DomainException>()
            .WithMessage("Judgment step 'judge' of contour 'sample' declares the source step 'act' twice; a step declares each source once.");
    }

    [Fact]
    public void A_contour_drafted_through_the_definitions_path_is_refused_by_the_same_rules()
    {
        Contour broken = With([From("judge")]);

        FluentActions.Invoking(() => DefinitionDecider.Draft(
                DefinitionKind.Contour, broken.Name, DefinitionScope.Project, "alpha", 0,
                Contour.CanonicalJson(broken), broken.Hash, null, "brian", DateTimeOffset.UnixEpoch, knownActions: Known))
            .Should().Throw<DomainException>().WithMessage("Judgment step 'judge' of contour 'sample' declares itself as a source*");
    }

    [Fact]
    public void Declared_sources_survive_the_canonical_json_and_are_part_of_the_hash()
    {
        Contour declared = With([From("act"), From("check")]);

        Contour read = Contour.FromJson(Contour.CanonicalJson(declared));

        read.Steps[1].Sources.Should().Equal(From("act"), From("check"));
        read.Hash.Should().Be(declared.Hash);
        string[] hashes = [With([]).Hash, declared.Hash, With([From("check"), From("act")]).Hash, With([From("act")]).Hash];
        hashes.Should().OnlyHaveUniqueItems("the sources and their order are part of the hash");
    }
}
