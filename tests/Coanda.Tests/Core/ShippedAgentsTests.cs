using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Software;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class ShippedAgentsTests
{
    [Fact]
    public void The_five_shipped_agents_exist_and_pass_validation()
    {
        ShippedAgents.All.Select(a => a.Name).Should().Equal("build", "reviewer-adversarial", "reviewer-conformance", "fix", "discovery");
        foreach (AgentDefinition agent in ShippedAgents.All)
        {
            FluentActions.Invoking(agent.Validate).Should().NotThrow(agent.Name);
        }
    }

    [Fact]
    public void Each_agent_names_its_answer_schema()
    {
        ShippedAgents.Build.AnswerSchema.Should().Be("build");
        ShippedAgents.ReviewerAdversarial.AnswerSchema.Should().Be("review");
        ShippedAgents.ReviewerConformance.AnswerSchema.Should().Be("review");
        ShippedAgents.Fix.AnswerSchema.Should().Be("fix");
        ShippedAgents.Discovery.AnswerSchema.Should().Be("discovery");
    }

    [Fact]
    public void Only_build_and_fix_hold_the_edit_tool()
    {
        ShippedAgents.All.Where(a => a.HoldsTool(ToolCatalog.Edit)).Select(a => a.Name).Should().Equal("build", "fix");
    }

    [Fact]
    public void Every_agent_names_the_claude_code_vendor()
    {
        ShippedAgents.All.Should().OnlyContain(a => a.Model.Vendor == "claude-code");
    }

    [Fact]
    public void Each_output_part_names_every_verdict_value_its_schema_allows_and_asks_for_one_json_document()
    {
        foreach (AgentDefinition agent in ShippedAgents.All)
        {
            foreach (string verdict in AnswerSchemas.Require(agent.AnswerSchema).Verdicts)
            {
                agent.Persona.Output.Should().Contain($"\"{verdict}\"", $"{agent.Name} must name the verdict {verdict}");
            }

            agent.Persona.Output.Should().Contain("JSON document");
        }
    }

    [Fact]
    public void Personas_are_plain_prose_with_no_marker_lines()
    {
        foreach (AgentDefinition agent in ShippedAgents.All)
        {
            AgentPersona persona = agent.Persona;
            foreach (string part in new[] { persona.Role, persona.Purpose, persona.Output, persona.Rules, persona.Prohibitions })
            {
                part.Should().NotContain("\n", agent.Name);
                part.Should().NotMatchRegex(@"^[A-Z][A-Z_\-]{2,}:", agent.Name);
                part.Should().NotContain("\u2014", agent.Name);
            }
        }
    }

    [Fact]
    public void Shipped_definitions_carry_the_agents_hashes()
    {
        ShippedDefinition.ShippedAgentDefinitions.Select(d => (d.Kind, d.Name, d.ContentHash))
            .Should().Equal(ShippedAgents.All.Select(a => (DefinitionKind.Agent, a.Name, a.Hash)));
        ShippedDefinition.All.Should().HaveCount(ShippedTypes.All.Count + ShippedFlows.All.Count + ShippedAgents.All.Count);
    }
}
