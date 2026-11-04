using Coanda.Cli;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Tests.Daemon;

public sealed class AgentListTests
{
    private static async Task RunAndStopAsync(NodeHome home, Func<DaemonClient, Task> act)
    {
        await using RunningDaemon daemon = await DaemonHost.StartAsync(home, 0, CancellationToken.None);
        using var client = new DaemonClient(home);
        await act(client);
        await client.StopAsync(CancellationToken.None);
        await daemon.WaitForShutdownAsync(CancellationToken.None);
    }

    private static async Task<List<Definition>> AgentsInStoreAsync(NodeHome home)
    {
        await using IDocumentStore store = StoreConfiguration.Open(home.DatabasePath);
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Definition> all = await session.Query<Definition>().ToListAsync(CancellationToken.None);
        return [.. all.Where(d => d.Kind == DefinitionKind.Agent)];
    }

    private static async Task<int> AgentEventCountAsync(NodeHome home)
    {
        await using IDocumentStore store = StoreConfiguration.Open(home.DatabasePath);
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        int events = 0;
        foreach (Definition definition in (await session.Query<Definition>().ToListAsync(CancellationToken.None)).Where(d => d.Kind == DefinitionKind.Agent))
        {
            events += (await session.Events.FetchStreamAsync(definition.Id, token: CancellationToken.None)).Count;
        }

        return events;
    }

    [Fact]
    public async Task A_fresh_node_home_holds_the_five_shipped_agents_published_and_a_second_start_appends_nothing()
    {
        using var temp = new TemporaryHome();

        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);

        List<Definition> held = await AgentsInStoreAsync(temp.Home);
        held.Should().HaveCount(5);
        held.Should().OnlyContain(d => d.Scope == DefinitionScope.Shipped && d.Status == DefinitionStatus.Published && d.VersionNumber == 1);
        held.OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => (d.Name, d.ContentHash))
            .Should().Equal(ShippedAgents.All.OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => (a.Name, a.Hash)));
        held.Should().OnlyContain(d => AgentDefinition.FromJson(d.Content).Hash == d.ContentHash);
        int events = await AgentEventCountAsync(temp.Home);
        events.Should().Be(10);

        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);

        (await AgentEventCountAsync(temp.Home)).Should().Be(events);
    }

    [Fact]
    public async Task The_agent_list_endpoint_shows_exactly_the_five_shipped_agents_on_a_fresh_node_home()
    {
        using var temp = new TemporaryHome();

        await RunAndStopAsync(temp.Home, async client =>
        {
            List<AgentView> agents = await client.GetAsync<List<AgentView>>("/agents?project=alpha", CancellationToken.None);

            agents.Select(a => (a.Name, a.Version, a.Model, a.Hash)).Should().BeEquivalentTo(
                ShippedAgents.All.Select(a => (a.Name, 1, a.Model, a.Hash)));
            agents.Should().HaveCount(5);
        });
    }

    [Fact]
    public async Task A_project_agent_published_in_one_project_shows_there_and_not_in_another()
    {
        using var temp = new TemporaryHome();
        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);

        AgentDefinition custom = ShippedAgents.Fix with { Persona = ShippedAgents.Fix.Persona with { Role = "A house-style fixer." } };
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);
            Definition draft = await definitions.DraftAsync(
                Principal.LocalOwner, DefinitionKind.Agent, DefinitionScope.Project, "alpha", "fix",
                AgentDefinition.CanonicalJson(custom), custom.Hash, new ForkBase("fix", 1), CancellationToken.None);
            await definitions.PublishAsync(Principal.LocalOwner, "alpha", draft.Id, CancellationToken.None);
        }

        await RunAndStopAsync(temp.Home, async client =>
        {
            List<AgentView> alpha = await client.GetAsync<List<AgentView>>("/agents?project=alpha", CancellationToken.None);
            List<AgentView> beta = await client.GetAsync<List<AgentView>>("/agents?project=beta", CancellationToken.None);

            alpha.Should().HaveCount(5);
            alpha.Single(a => a.Name == "fix").Hash.Should().Be(custom.Hash);
            beta.Single(a => a.Name == "fix").Hash.Should().Be(ShippedAgents.Fix.Hash);
        });
    }

    [Fact]
    public void Every_role_may_list_agents()
    {
        foreach (Role role in Enum.GetValues<Role>())
        {
            CommandRegistry.Require("agent.list", new Principal("someone", role)).Should().NotBeNull();
        }
    }
}
