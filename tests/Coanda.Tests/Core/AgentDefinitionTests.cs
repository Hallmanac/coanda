using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;

namespace Coanda.Tests.Core;

public sealed class AgentDefinitionTests
{
    private static AgentDefinition Agent() => new()
    {
        Name = "scribe",
        Persona = new AgentPersona("a role", "a purpose", "an output", "some rules", "some prohibitions"),
        Model = new ModelSpec("claude-code", "claude-sonnet-5-5", "high"),
        Tools = ["read", "search", "edit"],
        Skills = ["alpha-skill", "beta-skill"],
        McpServers = ["docs", "tracker"],
        AnswerSchema = "essay",
        AnswerSchemaContent = Content("""{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["drafted","abandoned"]}}}"""),
    };

    private static JsonElement Content(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Refused(AgentDefinition agent, string message)
    {
        DomainException exception = Assert.Throws<DomainException>(agent.Validate);
        Assert.Matches(WildcardPattern.ToRegex(message), exception.Message);
    }

    [Fact]
    public void A_complete_agent_is_valid()
    {
        Agent().Validate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_or_blank_name_is_refused(string? name) =>
        Refused(Agent() with { Name = name! }, "An agent needs a name.");

    [Theory]
    [InlineData("role")]
    [InlineData("purpose")]
    [InlineData("output")]
    [InlineData("rules")]
    [InlineData("prohibitions")]
    public void A_missing_or_blank_persona_part_is_refused(string part)
    {
        foreach (string? bad in new string?[] { null, "", "   " })
        {
            AgentPersona persona = Agent().Persona;
            AgentPersona broken = part switch
            {
                "role" => persona with { Role = bad! },
                "purpose" => persona with { Purpose = bad! },
                "output" => persona with { Output = bad! },
                "rules" => persona with { Rules = bad! },
                _ => persona with { Prohibitions = bad! },
            };

            Refused(Agent() with { Persona = broken }, $"Agent 'scribe' needs * in its persona.");
        }
    }

    [Theory]
    [InlineData("vendor", "a vendor")]
    [InlineData("model", "a model")]
    [InlineData("effort", "an effort")]
    public void A_model_spec_with_a_missing_or_blank_part_is_refused(string part, string phrase)
    {
        foreach (string? bad in new string?[] { null, "", "   " })
        {
            ModelSpec model = Agent().Model;
            ModelSpec broken = part switch
            {
                "vendor" => model with { Vendor = bad! },
                "model" => model with { Model = bad! },
                _ => model with { Effort = bad! },
            };

            Refused(Agent() with { Model = broken }, $"Agent 'scribe' needs {phrase} in its model spec.");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_answer_schema_name_is_refused(string name) =>
        Refused(Agent() with { AnswerSchema = name }, "Agent 'scribe' needs the name of an answer schema.");

    [Fact]
    public void An_answer_schema_named_outside_the_four_the_release_once_shipped_is_valid_with_its_content()
    {
        AgentDefinition agent = Agent();

        agent.Validate();
        Assert.Equal(new[] { "drafted", "abandoned" }, agent.ResolveAnswerSchema().Verdicts);
    }

    [Fact]
    public void A_tool_the_catalog_does_not_hold_is_refused() =>
        Refused(Agent() with { Tools = ["read", "teleport"] }, "Agent 'scribe' names the tool 'teleport', which the shipped tool catalog does not hold*");

    [Fact]
    public void A_blank_skill_or_mcp_server_name_is_refused()
    {
        Refused(Agent() with { Skills = ["alpha-skill", " "] }, "Agent 'scribe' lists a skill with a blank name.");
        Refused(Agent() with { McpServers = [""] }, "Agent 'scribe' lists an MCP server with a blank name.");
    }

    [Fact]
    public void A_name_listed_twice_is_refused()
    {
        Refused(Agent() with { Tools = ["read", "edit", "read"] }, "Agent 'scribe' lists the tool 'read' twice in its tool set.");
        Refused(Agent() with { Skills = ["a", "a"] }, "Agent 'scribe' lists the skill 'a' twice in its skills.");
        Refused(Agent() with { McpServers = ["docs", "docs"] }, "Agent 'scribe' lists the MCP server 'docs' twice in its MCP servers.");
    }

    [Fact]
    public void Two_agents_built_separately_from_the_same_data_hash_equal()
    {
        Assert.Equal(Agent().Hash, Agent().Hash);
    }

    [Fact]
    public void Reordering_the_tool_set_the_skills_or_the_servers_leaves_the_hash_unchanged()
    {
        AgentDefinition reordered = Agent() with
        {
            Tools = ["edit", "read", "search"],
            Skills = ["beta-skill", "alpha-skill"],
            McpServers = ["tracker", "docs"],
        };

        Assert.Equal(Agent().Hash, reordered.Hash);
    }

    [Fact]
    public void Changing_any_part_of_the_content_changes_the_hash()
    {
        AgentDefinition agent = Agent();
        AgentDefinition[] changed =
        [
            agent with { Name = "scribe2" },
            agent with { Persona = agent.Persona with { Role = "other" } },
            agent with { Persona = agent.Persona with { Purpose = "other" } },
            agent with { Persona = agent.Persona with { Output = "other" } },
            agent with { Persona = agent.Persona with { Rules = "other" } },
            agent with { Persona = agent.Persona with { Prohibitions = "other" } },
            agent with { Model = agent.Model with { Vendor = "other" } },
            agent with { Model = agent.Model with { Model = "other" } },
            agent with { Model = agent.Model with { Effort = "other" } },
            agent with { Tools = ["read", "search"] },
            agent with { Skills = ["alpha-skill"] },
            agent with { McpServers = ["docs"] },
            agent with { AnswerSchema = "review" },
            agent with { AnswerSchemaContent = Content("""{"type":"object","required":["verdict"],"properties":{"verdict":{"type":"string","enum":["drafted"]}}}""") },
        ];

        IEnumerable<string> actual = changed.Select(a => a.Hash);
        Assert.Distinct(actual);
        Assert.DoesNotContain(agent.Hash, actual);
    }

    [Fact]
    public void A_small_fixed_agent_hashes_to_a_known_value()
    {
        var agent = new AgentDefinition
        {
            Name = "tiny",
            Persona = new AgentPersona("r", "p", "o", "ru", "pr"),
            Model = new ModelSpec("v", "m", "e"),
            Tools = ["read"],
            Skills = ["s"],
            McpServers = ["x"],
            AnswerSchema = "fix",
        };

        Assert.Equal("bf46e78dca27f71e", agent.Hash);
    }

    [Fact]
    public void Canonical_json_round_trips_to_an_agent_with_the_same_hash()
    {
        foreach (AgentDefinition agent in SoftwareAgents.All)
        {
            AgentDefinition read = AgentDefinition.FromJson(AgentDefinition.CanonicalJson(agent));
            Assert.Equal(agent.Hash, read.Hash);
            read.Validate();
        }
    }
}
