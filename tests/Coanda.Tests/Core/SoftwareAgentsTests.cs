using System.Text.Json;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;

namespace Coanda.Tests.Core;

public sealed class SoftwareAgentsTests
{
    [Fact]
    public void The_five_shipped_agents_exist_and_pass_validation()
    {
        Assert.Equal(["build", "reviewer-adversarial", "reviewer-conformance", "fix", "discovery"], SoftwareAgents.All.Select(a => a.Name));
        foreach (AgentDefinition agent in SoftwareAgents.All)
        {
            Exception? refusal = Record.Exception(agent.Validate);
            Assert.True(refusal is null, $"{agent.Name}: {refusal?.Message}");
        }
    }

    [Fact]
    public void Each_agent_names_its_answer_schema()
    {
        Assert.Equal("build", SoftwareAgents.Build.AnswerSchema);
        Assert.Equal("review", SoftwareAgents.ReviewerAdversarial.AnswerSchema);
        Assert.Equal("review", SoftwareAgents.ReviewerConformance.AnswerSchema);
        Assert.Equal("fix", SoftwareAgents.Fix.AnswerSchema);
        Assert.Equal("discovery", SoftwareAgents.Discovery.AnswerSchema);
    }

    [Theory]
    [InlineData("build", NameOnlyAnswerSchemas.Build)]
    [InlineData("reviewer-adversarial", NameOnlyAnswerSchemas.Review)]
    [InlineData("reviewer-conformance", NameOnlyAnswerSchemas.Review)]
    [InlineData("fix", NameOnlyAnswerSchemas.Fix)]
    [InlineData("discovery", NameOnlyAnswerSchemas.Discovery)]
    public void Each_agent_holds_as_its_schema_content_the_document_the_release_shipped_under_its_schema_name(string agent, string schema)
    {
        AgentDefinition shipped = SoftwareAgents.All.Single(a => a.Name == agent);
        AnswerSchema named = NameOnlyAnswerSchemas.Find(schema) ?? throw new InvalidOperationException($"No schema '{schema}'.");
        using JsonDocument document = JsonDocument.Parse(named.Document);

        JsonElement content = shipped.AnswerSchemaContent ?? throw new InvalidOperationException($"Agent '{agent}' holds no schema content.");
        Assert.Equal(schema, shipped.AnswerSchema);
        Assert.True(JsonElement.DeepEquals(document.RootElement, content), $"{agent}: {content}");
        Assert.Equal(named.Verdicts, shipped.ResolveAnswerSchema().Verdicts);
    }

    /// <summary>
    /// The hash of each shipped agent before it carried its schema content, as the store fixtures and
    /// every node's earlier module versions hold it, and after.
    /// </summary>
    [Theory]
    [InlineData("build", "37dd84328f49ca25", "039f9385559d36aa")]
    [InlineData("reviewer-adversarial", "4ff7ac20b180df0d", "a05d63d65709b916")]
    [InlineData("reviewer-conformance", "9f867144783e855c", "be473f662ee5093e")]
    [InlineData("fix", "4ca4afe60ac4e100", "6052573ca3d9be35")]
    [InlineData("discovery", "e99e12f13d399792", "a22ef13e3dcd2ab0")]
    public void Each_agent_hashes_as_before_without_its_schema_content_and_anew_with_it(string agent, string before, string after)
    {
        AgentDefinition shipped = SoftwareAgents.All.Single(a => a.Name == agent);

        Assert.Equal(before, (shipped with { AnswerSchemaContent = null }).Hash);
        Assert.Equal(after, shipped.Hash);
    }

    [Fact]
    public void Only_build_and_fix_hold_the_edit_tool()
    {
        Assert.Equal(["build", "fix"], SoftwareAgents.All.Where(a => a.HoldsTool(ToolCatalog.Edit)).Select(a => a.Name));
    }

    [Fact]
    public void Every_agent_names_the_claude_code_vendor()
    {
        Assert.All(SoftwareAgents.All, a => Assert.True(a.Model.Vendor == "claude-code"));
    }

    [Fact]
    public void Each_output_part_names_every_verdict_value_its_schema_allows_and_asks_for_one_json_document()
    {
        foreach (AgentDefinition agent in SoftwareAgents.All)
        {
            foreach (string verdict in agent.ResolveAnswerSchema().Verdicts)
            {
                // {agent.Name} must name the verdict {verdict}
                Assert.Contains($"\"{verdict}\"", agent.Persona.Output, StringComparison.Ordinal);
            }

            Assert.Contains("JSON document", agent.Persona.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Personas_are_plain_prose_with_no_marker_lines()
    {
        foreach (AgentDefinition agent in SoftwareAgents.All)
        {
            AgentPersona persona = agent.Persona;
            foreach (string part in new[] { persona.Role, persona.Purpose, persona.Output, persona.Rules, persona.Prohibitions })
            {
                // each check names the agent, agent.Name, as its reason
                Assert.DoesNotContain("\n", part, StringComparison.Ordinal);
                Assert.DoesNotMatch(@"^[A-Z][A-Z_\-]{2,}:", part);
                Assert.DoesNotContain("\u2014", part, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_agent_is_sent_to_the_project_guidance_by_its_rules()
    {
        Assert.Contains("AGENTS.md", SoftwareAgents.ProjectGuidance, StringComparison.Ordinal);
        Assert.Contains("CLAUDE.md", SoftwareAgents.ProjectGuidance, StringComparison.Ordinal);
        foreach (AgentDefinition agent in SoftwareAgents.All)
        {
            // the check names the agent, agent.Name, as its reason
            Assert.Contains(SoftwareAgents.ProjectGuidance, agent.Persona.Rules, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Shipped_definitions_carry_the_agents_hashes()
    {
        Assert.Equal(SoftwareAgents.All.Select(a => (DefinitionKind.Agent, a.Name, a.Hash)), SoftwareDefinitions.Agents.Select(d => (d.Kind, d.Name, d.ContentHash)));
        Assert.Equal(SoftwareTypes.All.Count + SoftwareFlows.All.Count + SoftwareAgents.All.Count, SoftwareDefinitions.All.Count);
    }
}
