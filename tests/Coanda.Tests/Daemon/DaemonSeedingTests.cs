using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Contours;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
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
        await using RunningDaemon daemon = await Daemons.StartAsync(home, CancellationToken.None);
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
        afterFirst.Should().Be(ShippedDefinitions.All.Count * 2);

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
            var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);
            await RegisteredProjects.AddAsync(store, "alpha", CancellationToken.None);
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

    private static async Task<List<ContourView>> ContoursAsync(DaemonClient client, string project) =>
        await client.GetAsync<List<ContourView>>($"/contours?project={Uri.EscapeDataString(project)}", CancellationToken.None);

    [Fact]
    public async Task Starting_the_daemon_seeds_the_three_shipped_contours_as_published_shipped_scope_definitions_and_a_second_start_appends_nothing()
    {
        using var temp = new TemporaryHome();

        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);
        int afterFirst = await StreamCountAsync(temp.Home);
        afterFirst.Should().Be(ShippedDefinitions.All.Count * 2);

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await using IQuerySession session = store.QuerySession(Tenants.Node);
            IReadOnlyList<Definition> all = await session.Query<Definition>().ToListAsync(CancellationToken.None);
            List<Definition> contours = [.. all.Where(d => d.Kind == DefinitionKind.Contour)];
            contours.Select(d => (d.Name, d.ContentHash)).Should().BeEquivalentTo(ShippedContours.All.Select(f => (f.Name, f.Hash)));
            contours.Should().OnlyContain(d => d.Scope == DefinitionScope.Shipped && d.Status == DefinitionStatus.Published && d.VersionNumber == 1);
            contours.Should().OnlyContain(d => Contour.FromJson(d.Content).Hash == d.ContentHash);
        }

        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);
        (await StreamCountAsync(temp.Home)).Should().Be(afterFirst);
    }

    [Fact]
    public async Task The_contour_list_endpoint_returns_exactly_the_three_shipped_contours_on_a_fresh_node_home()
    {
        using var temp = new TemporaryHome();

        await RunAndStopAsync(temp.Home, async client =>
        {
            List<ContourView> contours = await ContoursAsync(client, "alpha");

            contours.Select(f => (f.Name, f.Version, f.Hash)).Should().Equal(
                ShippedContours.All.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => (f.Name, 1, f.Hash)));
            contours.Select(f => f.Name).Should().BeEquivalentTo("build", "discovery", "review-on-assignment");

            await FluentActions.Awaiting(() => client.GetAsync<List<ContourView>>("/contours?project=_node", CancellationToken.None))
                .Should().ThrowAsync<DomainException>().WithMessage("*reserved for node-level records*");
        });
    }

    [Fact]
    public async Task A_project_contour_overrides_the_shipped_one_in_that_project_only()
    {
        using var temp = new TemporaryHome();
        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);

        Contour quick = ShippedContours.ReviewOnAssignment with { Budget = 1m };
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);
            Definition draft = await definitions.DraftAsync(
                Principal.LocalOwner, DefinitionKind.Contour, DefinitionScope.Project, "alpha",
                quick.Name, Contour.CanonicalJson(quick), quick.Hash, null, CancellationToken.None);
            await definitions.PublishAsync(Principal.LocalOwner, "alpha", draft.Id, CancellationToken.None);
        }

        await RunAndStopAsync(temp.Home, async client =>
        {
            (await ContoursAsync(client, "alpha")).Single(f => f.Name == "review-on-assignment").Hash.Should().Be(quick.Hash);
            (await ContoursAsync(client, "beta")).Single(f => f.Name == "review-on-assignment").Hash.Should().Be(ShippedContours.ReviewOnAssignment.Hash);
        });
    }

    [Fact]
    public void The_contour_list_query_is_open_to_every_role()
    {
        foreach (Role role in Enum.GetValues<Role>())
        {
            CommandRegistry.Require("contour.list", new Principal("someone", role)).Kind.Should().Be(EntryKind.Query);
        }
    }

    [Fact]
    public async Task The_build_contours_new_hash_seeds_as_the_next_version_of_the_one_without_field_declarations()
    {
        using var temp = new TemporaryHome();
        Contour before = ShippedContours.Build with { Steps = [.. ShippedContours.Build.Steps.Select(s => s with { Fields = [] })] };
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, [ShippedDefinition.FromContour(before)]);
        var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);

        IReadOnlyList<Definition> seeded = await definitions.SeedAsync(ShippedDefinitions.All, CancellationToken.None);

        Definition build = seeded.Single(d => d.Kind == DefinitionKind.Contour && d.Name == "build");
        build.VersionNumber.Should().Be(2);
        build.ContentHash.Should().Be(ShippedContours.Build.Hash);
    }

    [Fact]
    public async Task The_build_contours_new_hash_seeds_as_the_next_version_of_the_one_without_source_declarations()
    {
        using var temp = new TemporaryHome();
        Contour before = ShippedContours.Build with { Steps = [.. ShippedContours.Build.Steps.Select(s => s with { Sources = [] })] };
        before.Hash.Should().NotBe(ShippedContours.Build.Hash);
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, [ShippedDefinition.FromContour(before)]);
        var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);

        IReadOnlyList<Definition> seeded = await definitions.SeedAsync(ShippedDefinitions.All, CancellationToken.None);

        Definition build = seeded.Single(d => d.Kind == DefinitionKind.Contour && d.Name == "build");
        build.VersionNumber.Should().Be(2);
        build.ContentHash.Should().Be(ShippedContours.Build.Hash);
    }
}
