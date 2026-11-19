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
        McpServers = [],
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
    public void A_blank_skill_name_is_refused() =>
        Refused(Agent() with { Skills = ["alpha-skill", " "] }, "Agent 'scribe' lists a skill with a blank name.");

    [Fact]
    public void An_agent_that_names_any_mcp_server_is_refused()
    {
        Refused(
            Agent() with { McpServers = ["docs"] },
            "Agent 'scribe' names the MCP servers 'docs'; agent sessions load no MCP server yet, so an agent's MCP servers list must be empty.");
        Refused(
            Agent() with { McpServers = ["docs", "tracker"] },
            "Agent 'scribe' names the MCP servers 'docs', 'tracker'; agent sessions load no MCP server yet, so an agent's MCP servers list must be empty.");
        Refused(Agent() with { McpServers = [""] }, "Agent 'scribe' names the MCP servers ''; agent sessions load no MCP server yet*");
        Refused(Agent() with { McpServers = ["docs", "docs"] }, "Agent 'scribe' names the MCP servers 'docs', 'docs'; agent sessions load no MCP server yet*");
    }

    [Fact]
    public void Content_stored_with_a_named_mcp_server_still_reads_and_hashes()
    {
        AgentDefinition stored = Agent() with { McpServers = ["tracker", "docs"] };

        AgentDefinition read = AgentDefinition.FromJson(AgentDefinition.CanonicalJson(stored));

        Assert.Equal(new[] { "tracker", "docs" }, read.McpServers);
        Assert.Equal(stored.Hash, read.Hash);
        Assert.NotEqual(Agent().Hash, read.Hash);
    }

    [Fact]
    public void A_name_listed_twice_is_refused()
    {
        Refused(Agent() with { Tools = ["read", "edit", "read"] }, "Agent 'scribe' lists the tool 'read' twice in its tool set.");
        Refused(Agent() with { Skills = ["a", "a"] }, "Agent 'scribe' lists the skill 'a' twice in its skills.");
    }

    [Fact]
    public void Two_agents_built_separately_from_the_same_data_hash_equal()
    {
        Assert.Equal(Agent().Hash, Agent().Hash);
    }

    [Fact]
    public void Reordering_the_tool_set_the_skills_or_the_servers_leaves_the_hash_unchanged()
    {
        // The hash covers the servers although a draft refuses any, so both sides name some.
        AgentDefinition agent = Agent() with { McpServers = ["docs", "tracker"] };
        AgentDefinition reordered = agent with
        {
            Tools = ["edit", "read", "search"],
            Skills = ["beta-skill", "alpha-skill"],
            McpServers = ["tracker", "docs"],
        };

        Assert.Equal(agent.Hash, reordered.Hash);
    }

    [Fact]
    public void Changing_any_part_of_the_content_changes_the_hash()
    {
        AgentDefinition agent = Agent() with { McpServers = ["docs", "tracker"] };
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
            agent with { Confined = true },
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
        Assert.Equal("bf46e78dca27f71e", (agent with { Confined = false }).Hash);
        Assert.NotEqual("bf46e78dca27f71e", (agent with { Confined = true }).Hash);
    }

    [Fact]
    public void A_confined_agent_reads_back_from_its_canonical_json_confined_and_with_the_same_hash()
    {
        AgentDefinition confined = Agent() with { Confined = true };

        string json = AgentDefinition.CanonicalJson(confined);
        AgentDefinition read = AgentDefinition.FromJson(json);

        Assert.Contains("\"Confined\": true", json, StringComparison.Ordinal);
        Assert.True(read.Confined);
        Assert.Equal(confined.Hash, read.Hash);
        Assert.NotEqual(Agent().Hash, read.Hash);
    }

    [Fact]
    public void An_agent_that_does_not_declare_itself_confined_writes_no_confined_member_and_hashes_as_before()
    {
        AgentDefinition unconfined = Agent();
        AgentDefinition declaredFalse = AgentDefinition.FromJson(
            AgentDefinition.CanonicalJson(unconfined).Replace("\"Name\":", "\"Confined\": false,\n  \"Name\":", StringComparison.Ordinal));

        Assert.False(declaredFalse.Confined);
        Assert.Equal(unconfined.Hash, declaredFalse.Hash);
        Assert.DoesNotContain("Confined", AgentDefinition.CanonicalJson(unconfined), StringComparison.Ordinal);
        Assert.DoesNotContain("Confined", AgentDefinition.CanonicalJson(declaredFalse), StringComparison.Ordinal);
        Assert.Equal(AgentDefinition.CanonicalJson(unconfined), AgentDefinition.CanonicalJson(declaredFalse));
    }

    [Theory]
    [InlineData("confined")]
    [InlineData("Confine")]
    [InlineData("Confinded")]
    public void A_member_the_agent_shape_does_not_define_is_refused_by_name_rather_than_ignored(string member)
    {
        string json = AgentDefinition.CanonicalJson(Agent() with { Tools = ["read", "shell"] })
            .Replace("\"Name\":", $"\"{member}\": true,\n  \"Name\":", StringComparison.Ordinal);

        DomainException refused = Assert.Throws<DomainException>(() => AgentDefinition.FromJson(json));

        Assert.StartsWith("The content cannot be read as an agent definition:", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"'{member}'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_the_persona_or_the_model_spec_does_not_define_is_refused()
    {
        string json = AgentDefinition.CanonicalJson(Agent())
            .Replace("\"Role\":", "\"Mood\": \"calm\",\n    \"Role\":", StringComparison.Ordinal);

        DomainException refused = Assert.Throws<DomainException>(() => AgentDefinition.FromJson(json));

        Assert.Contains("'Mood'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_confined_agent_that_holds_the_shell_or_the_web_tool_is_refused_naming_each()
    {
        Refused(
            Agent() with { Confined = true, Tools = ["read", "shell"] },
            "Agent 'scribe' is confined and holds the shell tool; a confined agent holds neither the shell tool nor the web tool.");
        Refused(
            Agent() with { Confined = true, Tools = ["web", "read"] },
            "Agent 'scribe' is confined and holds the web tool; a confined agent holds neither the shell tool nor the web tool.");
        Refused(
            Agent() with { Confined = true, Tools = ["web", "edit", "shell"] },
            "Agent 'scribe' is confined and holds the shell tool and the web tool; a confined agent holds neither the shell tool nor the web tool.");
    }

    [Fact]
    public void A_confined_agent_that_holds_only_read_search_or_edit_is_valid()
    {
        (Agent() with { Confined = true }).Validate();
        (Agent() with { Confined = true, Tools = [] }).Validate();
        (Agent() with { Confined = true, Tools = ["read"] }).Validate();
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
