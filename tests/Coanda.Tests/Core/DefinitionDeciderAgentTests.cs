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
        AgentDefinition agent = SoftwareAgents.Fix;

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
        AgentDefinition broken = SoftwareAgents.Fix with { Tools = ["teleport"] };

        DomainException exception = Assert.Throws<DomainException>(() => Draft("fix", AgentDefinition.CanonicalJson(broken), broken.Hash));
        Assert.StartsWith("Agent 'fix' names the tool 'teleport'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_that_names_an_mcp_server_is_refused_when_drafted_and_when_imported()
    {
        AgentDefinition named = SoftwareAgents.Fix with { McpServers = ["tracker"] };
        string expected = "Agent 'fix' names the MCP servers 'tracker'; agent sessions load no MCP server yet, so an agent's MCP servers list must be empty.";

        DomainException drafted = Assert.Throws<DomainException>(() => Draft("fix", AgentDefinition.CanonicalJson(named), named.Hash));
        Assert.Equal(expected, drafted.Message);

        DomainException imported = Assert.Throws<DomainException>(() => DefinitionDecider.Import(ModuleDefinition.FromAgent(named), "software", "0.1.0", [], "import", Now));
        Assert.Equal(expected, imported.Message);
    }

    [Fact]
    public void A_confined_agent_that_holds_the_shell_or_the_web_tool_is_refused_when_drafted_and_when_imported()
    {
        AgentDefinition confined = SoftwareAgents.Fix with { Confined = true, Tools = ["read", "shell", "web"] };
        string expected = "Agent 'fix' is confined and holds the shell tool and the web tool; a confined agent holds neither the shell tool nor the web tool.";

        DomainException drafted = Assert.Throws<DomainException>(() => Draft("fix", AgentDefinition.CanonicalJson(confined), confined.Hash));
        Assert.Equal(expected, drafted.Message);

        DomainException imported = Assert.Throws<DomainException>(() => DefinitionDecider.Import(ModuleDefinition.FromAgent(confined), "software", "0.1.0", [], "import", Now));
        Assert.Equal(expected, imported.Message);
    }

    [Fact]
    public void A_confined_agent_that_holds_only_read_search_or_edit_is_drafted_and_imported()
    {
        AgentDefinition confined = SoftwareAgents.Fix with { Confined = true, Tools = ["read", "search", "edit"] };

        Definition definition = Definition.Create(Draft("fix", AgentDefinition.CanonicalJson(confined), confined.Hash));
        Assert.Equal(confined.Hash, definition.ContentHash);
        Assert.True(AgentDefinition.FromJson(definition.Content).Confined);

        (DefinitionDrafted imported, DefinitionPublished _) =
            DefinitionDecider.Import(ModuleDefinition.FromAgent(confined), "software", "0.1.0", [], "import", Now) ?? throw new InvalidOperationException("Nothing was imported.");
        Assert.Equal(confined.Hash, imported.ContentHash);
        Assert.True(AgentDefinition.FromJson(imported.Content).Confined);
    }

    [Fact]
    public void A_content_hash_that_differs_from_the_agents_hash_is_refused()
    {
        AgentDefinition agent = SoftwareAgents.Fix;

        DomainException exception = Assert.Throws<DomainException>(() => Draft("fix", AgentDefinition.CanonicalJson(agent), SoftwareAgents.Build.Hash));
        Assert.Equal($"The content hash '{SoftwareAgents.Build.Hash}' differs from the agent's hash '{agent.Hash}'.", exception.Message);
    }

    [Fact]
    public void A_definition_named_for_a_different_agent_is_refused()
    {
        AgentDefinition agent = SoftwareAgents.Fix;

        DomainException exception = Assert.Throws<DomainException>(() => Draft("build", AgentDefinition.CanonicalJson(agent), agent.Hash));
        Assert.StartsWith("An agent definition is named for its agent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_an_agent_produces_published_module_scope_events()
    {
        ModuleDefinition discovery = ModuleDefinition.FromAgent(SoftwareAgents.Discovery);

        (DefinitionDrafted drafted, DefinitionPublished _) =
            DefinitionDecider.Import(discovery, "software", "0.1.0", [], "import", Now) ?? throw new InvalidOperationException("Nothing was imported.");

        Assert.Equal(DefinitionKind.Agent, drafted.Kind);
        Assert.Equal(DefinitionScope.Module, drafted.Scope);
        Assert.Equal(SoftwareAgents.Discovery.Hash, drafted.ContentHash);
    }
}
