using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Tests.Daemon;

public sealed class DaemonSeedingTests
{
    private static async Task<List<TypeView>> TypesAsync(DaemonClient client, string project) =>
        await client.GetAsync<List<TypeView>>($"/types?project={Uri.EscapeDataString(project)}", CancellationToken.None);

    private static async Task RunAndStopAsync(NodeHome home, Func<DaemonClient, Task> act)
    {
        await using RunningDaemon daemon = await DaemonHost.StartAsync(home, 0, CancellationToken.None);
        using var client = new DaemonClient(home);
        await act(client);
        await client.StopAsync(CancellationToken.None);
        await daemon.WaitForShutdownAsync(CancellationToken.None);
    }

    private static async Task<int> StreamCountAsync(NodeHome home)
    {
        await using IDocumentStore store = StoreConfiguration.Open(home.DatabasePath);
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        int events = 0;
        foreach (Definition definition in await session.Query<Definition>().ToListAsync(CancellationToken.None))
        {
            events += (await session.Events.FetchStreamAsync(definition.Id, token: CancellationToken.None)).Count;
        }

        return events;
    }

    [Fact]
    public async Task Starting_the_daemon_seeds_the_shipped_types_and_a_second_start_appends_nothing()
    {
        using var temp = new TemporaryHome();

        await RunAndStopAsync(temp.Home, async client =>
        {
            List<TypeView> types = await TypesAsync(client, "alpha");
            types.Select(t => (t.Name, t.Hash)).Should().Equal(ShippedTypes.All.Select(t => (t.Name, t.Hash)));
        });
        int afterFirst = await StreamCountAsync(temp.Home);
        afterFirst.Should().Be(ShippedTypes.All.Count * 2);

        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);
        (await StreamCountAsync(temp.Home)).Should().Be(afterFirst);
    }

    [Fact]
    public async Task The_type_list_endpoint_takes_a_project_and_shows_that_projects_own_types()
    {
        using var temp = new TemporaryHome();
        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);

        ParcelType qa = ShippedTypes.Task with { EntryStates = ["refine", "build"] };
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var definitions = new DefinitionService(store, TimeProvider.System);
            Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, "alpha", qa, null, CancellationToken.None);
            await definitions.PublishAsync(Principal.LocalOwner, "alpha", draft.Id, CancellationToken.None);
        }

        await RunAndStopAsync(temp.Home, async client =>
        {
            (await TypesAsync(client, "alpha")).Single(t => t.Name == "task").Hash.Should().Be(qa.Hash);
            (await TypesAsync(client, "beta")).Single(t => t.Name == "task").Hash.Should().Be(ShippedTypes.Task.Hash);

            ParcelView added = await client.PostAsync<ParcelView>(
                "/parcels", new ParcelAddRequest("alpha", "task", "Starts building", "build"), CancellationToken.None);
            added.TypeHash.Should().Be(qa.Hash);

            await FluentActions.Awaiting(() => client.GetAsync<List<TypeView>>("/types?project=_node", CancellationToken.None))
                .Should().ThrowAsync<DomainException>().WithMessage("*reserved for node-level records*");
        });
    }
}
