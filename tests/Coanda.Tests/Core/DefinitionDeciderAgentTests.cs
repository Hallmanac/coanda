using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Software;

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

        Assert.Equal(DefinitionKind.Agent, definition.Kind);
        Assert.Equal(agent.Hash, definition.ContentHash);
        Assert.Equal(DefinitionStatus.Draft, definition.Status);
    }

    [Fact]
    public void Content_that_cannot_be_read_as_an_agent_is_refused()
    {
        foreach (string content in new[] { "not json", "{}", "null", """{"Name":"fix","Persona":null}""" })
        {
            DomainException exception = Assert.Throws<DomainException>(() => Draft("fix", content, "abc"));
            Assert.Matches(@"(?s)^The content.*agent definition.*$", exception.Message);
        }
    }

    [Fact]
    public void An_agent_that_fails_validation_is_refused_quoting_the_validation_message()
    {
        AgentDefinition broken = ShippedAgents.Fix with { AnswerSchema = "essay" };

        DomainException exception = Assert.Throws<DomainException>(() => Draft("fix", AgentDefinition.CanonicalJson(broken), broken.Hash));
        Assert.StartsWith("Agent 'fix' names the answer schema 'essay'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_content_hash_that_differs_from_the_agents_hash_is_refused()
    {
        AgentDefinition agent = ShippedAgents.Fix;

        DomainException exception = Assert.Throws<DomainException>(() => Draft("fix", AgentDefinition.CanonicalJson(agent), ShippedAgents.Build.Hash));
        Assert.Equal($"The content hash '{ShippedAgents.Build.Hash}' differs from the agent's hash '{agent.Hash}'.", exception.Message);
    }

    [Fact]
    public void A_definition_named_for_a_different_agent_is_refused()
    {
        AgentDefinition agent = ShippedAgents.Fix;

        DomainException exception = Assert.Throws<DomainException>(() => Draft("build", AgentDefinition.CanonicalJson(agent), agent.Hash));
        Assert.StartsWith("An agent definition is named for its agent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Seeding_an_agent_produces_published_shipped_events()
    {
        ShippedDefinition shipped = ShippedDefinition.FromAgent(ShippedAgents.Discovery);

        (DefinitionDrafted drafted, DefinitionPublished _) = DefinitionDecider.Seed(shipped, 0, "seed", Now);

        Assert.Equal(DefinitionKind.Agent, drafted.Kind);
        Assert.Equal(DefinitionScope.Shipped, drafted.Scope);
        Assert.Equal(ShippedAgents.Discovery.Hash, drafted.ContentHash);
    }
}
