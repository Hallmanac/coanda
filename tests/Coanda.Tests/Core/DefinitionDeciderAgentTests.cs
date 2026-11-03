using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;

namespace Coanda.Tests.Core;

public sealed class DefinitionDeciderAgentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static DefinitionDrafted Draft(string name, string content, string hash) =>
        DefinitionDecider.Draft(DefinitionKind.Agent, name, DefinitionScope.Project, "alpha", 0, content, hash, null, "brian", Now);

    [Fact]
    public void A_valid_agent_is_drafted_with_its_content_and_hash()
    {
        AgentDefinition agent = ShippedAgents.Fix;

        Definition definition = Definition.Create(Draft("fix", AgentDefinition.CanonicalJson(agent), agent.Hash));

        definition.Kind.Should().Be(DefinitionKind.Agent);
        definition.ContentHash.Should().Be(agent.Hash);
        definition.Status.Should().Be(DefinitionStatus.Draft);
    }

    [Fact]
    public void Content_that_cannot_be_read_as_an_agent_is_refused()
    {
        foreach (string content in new[] { "not json", "{}", "null", """{"Name":"fix","Persona":null}""" })
        {
            FluentActions.Invoking(() => Draft("fix", content, "abc"))
                .Should().Throw<DomainException>().WithMessage("The content*agent definition*");
        }
    }

    [Fact]
    public void An_agent_that_fails_validation_is_refused_quoting_the_validation_message()
    {
        AgentDefinition broken = ShippedAgents.Fix with { AnswerSchema = "essay" };

        FluentActions.Invoking(() => Draft("fix", AgentDefinition.CanonicalJson(broken), broken.Hash))
            .Should().Throw<DomainException>().WithMessage("Agent 'fix' names the answer schema 'essay'*");
    }

    [Fact]
    public void A_content_hash_that_differs_from_the_agents_hash_is_refused()
    {
        AgentDefinition agent = ShippedAgents.Fix;

        FluentActions.Invoking(() => Draft("fix", AgentDefinition.CanonicalJson(agent), ShippedAgents.Build.Hash))
            .Should().Throw<DomainException>().WithMessage($"The content hash '{ShippedAgents.Build.Hash}' differs from the agent's hash '{agent.Hash}'.");
    }

    [Fact]
    public void A_definition_named_for_a_different_agent_is_refused()
    {
        AgentDefinition agent = ShippedAgents.Fix;

        FluentActions.Invoking(() => Draft("build", AgentDefinition.CanonicalJson(agent), agent.Hash))
            .Should().Throw<DomainException>().WithMessage("An agent definition is named for its agent*");
    }

    [Fact]
    public void Seeding_an_agent_produces_published_shipped_events()
    {
        ShippedDefinition shipped = ShippedDefinition.FromAgent(ShippedAgents.Discovery);

        (DefinitionDrafted drafted, DefinitionPublished _) = DefinitionDecider.Seed(shipped, 0, "seed", Now);

        drafted.Kind.Should().Be(DefinitionKind.Agent);
        drafted.Scope.Should().Be(DefinitionScope.Shipped);
        drafted.ContentHash.Should().Be(ShippedAgents.Discovery.Hash);
    }
}
