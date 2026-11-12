using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;

namespace Coanda.Tests.Core;

/// <summary>
/// An agent carries its answer schema as JSON Schema content: the rules that content keeps when the
/// agent is drafted or imported, how it is hashed, and how agent content stored before agents carried
/// any still reads.
/// </summary>
public sealed class AgentAnswerSchemaContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private const string Conforming =
        """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["not-for-me","taken"]},"summary":{"type":"string"}}}""";

    private static AgentDefinition Agent(string? content) => new()
    {
        Name = "triage",
        Persona = new AgentPersona("a role", "a purpose", "an output", "some rules", "some prohibitions"),
        Model = new ModelSpec("claude-code", "claude-sonnet-5-5", "high"),
        Tools = ["read"],
        Skills = [],
        McpServers = [],
        AnswerSchema = "triage",
        AnswerSchemaContent = content is null ? null : Content(content),
    };

    private static JsonElement Content(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static DefinitionDrafted Draft(AgentDefinition agent) =>
        DefinitionDecider.Draft(DefinitionKind.Agent, agent.Name, DefinitionScope.Project, "alpha", 0, AgentDefinition.CanonicalJson(agent), agent.Hash, null, "brian", Now);

    private static DefinitionDrafted Import(AgentDefinition agent) =>
        (DefinitionDecider.Import(ModuleDefinition.FromAgent(agent), "software", "0.2.0", [], "import", Now)
            ?? throw new InvalidOperationException("Nothing was imported.")).Drafted;

    /// <summary>Both ways an agent enters the store refuse it with the same message.</summary>
    private static void Refused(AgentDefinition agent, string message)
    {
        foreach (Func<AgentDefinition, DefinitionDrafted> enter in new Func<AgentDefinition, DefinitionDrafted>[] { Draft, Import })
        {
            DomainException exception = Assert.Throws<DomainException>(() => enter(agent));
            Assert.Matches(WildcardPattern.ToRegex(message), exception.Message);
        }
    }

    public static TheoryData<string, string> Broken => new()
    {
        { "\"triage\"", "Agent 'triage' holds answer schema content that is a string, not a JSON object." },
        { "[]", "Agent 'triage' holds answer schema content that is an array, not a JSON object." },
        { "3", "Agent 'triage' holds answer schema content that is a number, not a JSON object." },
        {
            """{"type":"object","required":["verdict"],"properties":{"verdict":{"$ref":"#/definitions/missing"}}}""",
            "Agent 'triage' holds answer schema content that is refused by the JSON Schema reader: *"
        },
        {
            """{"type":"object","type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["taken"]}}}""",
            "Agent 'triage' holds answer schema content that names the member 'type' twice in one object."
        },
        {
            """{"required":["verdict"],"properties":{"verdict":{"type":"string","enum":["taken"]}}}""",
            "Agent 'triage' holds answer schema content that does not declare \"type\": \"object\" at its top level."
        },
        {
            """{"type":"array","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["taken"]}}}""",
            "Agent 'triage' holds answer schema content that does not declare \"type\": \"object\" at its top level."
        },
        {
            """{"type":"object","properties":{"verdict":{"type":"string","enum":["taken"]}}}""",
            "Agent 'triage' holds answer schema content that does not list 'verdict' in its required array."
        },
        {
            """{"type":"object","required":["summary"],"properties":{"verdict":{"type":"string","enum":["taken"]}}}""",
            "Agent 'triage' holds answer schema content that does not list 'verdict' in its required array."
        },
        {
            """{"type":"object","required":["verdict"]}""",
            "Agent 'triage' holds answer schema content that does not declare 'verdict' as \"type\": \"string\" among its properties."
        },
        {
            """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"integer","enum":[1]}}}""",
            "Agent 'triage' holds answer schema content that does not declare 'verdict' as \"type\": \"string\" among its properties."
        },
        {
            """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string"}}}""",
            "Agent 'triage' holds answer schema content that does not give 'verdict' an enum that is a non-empty array of verdicts."
        },
        {
            """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":[]}}}""",
            "Agent 'triage' holds answer schema content that does not give 'verdict' an enum that is a non-empty array of verdicts."
        },
        {
            """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["taken","  "]}}}""",
            "Agent 'triage' holds answer schema content that lists a verdict in the enum of 'verdict' that is not a non-blank string."
        },
        {
            """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["taken",3]}}}""",
            "Agent 'triage' holds answer schema content that lists a verdict in the enum of 'verdict' that is not a non-blank string."
        },
        {
            """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["taken","taken"]}}}""",
            "Agent 'triage' holds answer schema content that lists the verdict 'taken' twice in the enum of 'verdict'."
        },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void Content_that_breaks_a_rule_is_refused_at_draft_and_at_import_naming_the_rule(string content, string message) =>
        Refused(Agent(content), message);

    [Fact]
    public void An_agent_holding_no_schema_content_is_refused_at_draft_and_at_import() =>
        Refused(
            Agent(null) with { AnswerSchema = NameOnlyAnswerSchemas.Build },
            "Agent 'triage' holds no answer schema content; an agent carries its answer schema 'build' as JSON Schema content beside its name.");

    [Theory]
    [InlineData("http://example.com/answer.json")]
    [InlineData("https://example.com/answer.json#/definitions/verdict")]
    [InlineData("/etc/coanda/answer.json")]
    [InlineData("answer.json")]
    [InlineData("file:///etc/coanda/answer.json")]
    public void A_ref_that_is_not_a_pointer_into_the_same_content_is_refused(string reference)
    {
        string content = """{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["taken"]},"detail":{"$ref":"""
            + JsonSerializer.Serialize(reference)
            + "}}}";

        Refused(
            Agent(content),
            $"Agent 'triage' holds answer schema content that holds the $ref \"{reference}\", which is not a JSON pointer into the same content (one that begins with '#'); an answer schema resolves no reference outside itself.");
    }

    [Fact]
    public void A_ref_that_is_not_a_string_is_refused() =>
        Refused(
            Agent("""{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["taken"]},"detail":{"$ref":3}}}"""),
            "Agent 'triage' holds answer schema content that holds the $ref 3, which is not a JSON pointer *");

    [Fact]
    public void A_conforming_agent_is_drafted_and_imported_and_answers_with_its_own_verdicts()
    {
        AgentDefinition agent = Agent(Conforming);

        Assert.Equal(agent.Hash, Definition.Create(Draft(agent)).ContentHash);
        Assert.Equal(agent.Hash, Definition.Create(Import(agent)).ContentHash);

        AnswerSchema schema = agent.ResolveAnswerSchema();
        Assert.Equal("triage", schema.Name);
        Assert.Equal(["not-for-me", "taken"], schema.Verdicts);
        Assert.True(AnswerValidator.Validate(schema, """{"verdict":"not-for-me","summary":"Not mine."}""").IsValid);
        Assert.False(AnswerValidator.Validate(schema, """{"verdict":"done"}""").IsValid);
    }

    [Fact]
    public void A_schema_whose_ref_points_within_itself_is_accepted_and_validates_an_answer_through_it()
    {
        AgentDefinition agent = Agent(
            """{"type":"object","required":["verdict"],"definitions":{"note":{"type":"string","maxLength":5}},"properties":{"verdict":{"type":"string","enum":["taken"]},"note":{"$ref":"#/definitions/note"}}}""");

        Definition.Create(Draft(agent));
        Definition.Create(Import(agent));

        AnswerSchema schema = agent.ResolveAnswerSchema();
        Assert.True(AnswerValidator.Validate(schema, """{"verdict":"taken","note":"short"}""").IsValid);
        Assert.False(AnswerValidator.Validate(schema, """{"verdict":"taken","note":"far too long"}""").IsValid);
    }

    [Fact]
    public void Two_agents_that_differ_only_in_their_schema_content_hash_differently()
    {
        AgentDefinition one = Agent(Conforming);
        AgentDefinition other = Agent(Conforming.Replace("\"taken\"", "\"held\"", StringComparison.Ordinal));

        Assert.NotEqual(one.Hash, other.Hash);
        Assert.NotEqual(one.Hash, (one with { AnswerSchemaContent = null }).Hash);
    }

    [Fact]
    public void The_order_of_a_schema_objects_members_leaves_the_hash_unchanged()
    {
        AgentDefinition reordered = Agent(
            """{"properties":{"summary":{"type":"string"},"verdict":{"enum":["not-for-me","taken"],"type":"string"}},"required":["verdict"],"type":"object"}""");

        Assert.Equal(Agent(Conforming).Hash, reordered.Hash);
    }

    [Fact]
    public void Canonical_json_of_an_agent_with_content_round_trips_to_the_same_hash()
    {
        AgentDefinition agent = Agent(Conforming);

        AgentDefinition read = AgentDefinition.FromJson(AgentDefinition.CanonicalJson(agent));

        Assert.Equal(agent.Hash, read.Hash);
        read.Validate();
    }

    /// <summary>
    /// The software module's build agent as the store held it at 2d1e82f, before agents carried
    /// schema content, with the hash it was stored under there (the store fixtures hold the same).
    /// </summary>
    private const string StoredBuild = "37dd84328f49ca25";

    [Fact]
    public void Agent_content_stored_before_agents_carried_a_schema_reads_hashes_and_validates_as_it_did()
    {
        AgentDefinition stored = AgentDefinition.FromJson(AgentDefinition.CanonicalJson(SoftwareAgents.Build with { AnswerSchemaContent = null }));

        Assert.Null(stored.AnswerSchemaContent);
        Assert.Equal(StoredBuild, stored.Hash);
        Assert.DoesNotContain("AnswerSchemaContent", AgentDefinition.CanonicalJson(stored), StringComparison.Ordinal);

        AnswerSchema schema = stored.ResolveAnswerSchema();
        Assert.Same(NameOnlyAnswerSchemas.Find(NameOnlyAnswerSchemas.Build), schema);
        Assert.True(AnswerValidator.Validate(schema, """{"verdict":"done"}""").IsValid);
        Assert.False(AnswerValidator.Validate(schema, """{"verdict":"not-for-me"}""").IsValid);
    }

    [Fact]
    public void Stored_content_naming_no_schema_the_release_once_shipped_and_holding_none_does_not_resolve()
    {
        DomainException exception = Assert.Throws<DomainException>(() => Agent(null).ResolveAnswerSchema());

        Assert.StartsWith("Agent 'triage' holds no answer schema content and names the answer schema 'triage'", exception.Message, StringComparison.Ordinal);
    }
}
