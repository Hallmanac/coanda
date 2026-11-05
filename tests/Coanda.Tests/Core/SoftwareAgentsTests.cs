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
            foreach (string verdict in AnswerSchemas.Require(agent.AnswerSchema).Verdicts)
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
