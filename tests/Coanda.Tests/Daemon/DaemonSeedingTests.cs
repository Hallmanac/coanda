using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Coanda.Tests.Core;
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
            Assert.Equal(ShippedTypes.All.Select(t => (t.Name, t.Hash)), types.Select(t => (t.Name, t.Hash)));
        });
        int afterFirst = await StreamCountAsync(temp.Home);
        Assert.Equal(ShippedDefinitions.All.Count * 2, afterFirst);

        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);
        Assert.Equal(afterFirst, await StreamCountAsync(temp.Home));
    }

    [Fact]
    public async Task The_type_list_endpoint_takes_a_project_and_shows_that_projects_own_types()
    {
        using var temp = new TemporaryHome();
        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);

        ParcelType qa = ShippedTypes.Task with { EntryStages = ["refine", "build"] };
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);
            await RegisteredProjects.AddAsync(store, "alpha", CancellationToken.None);
            Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, "alpha", qa, null, CancellationToken.None);
            await definitions.PublishAsync(Principal.LocalOwner, "alpha", draft.Id, CancellationToken.None);
        }

        await RunAndStopAsync(temp.Home, async client =>
        {
            Assert.Equal(qa.Hash, (await TypesAsync(client, "alpha")).Single(t => t.Name == "task").Hash);
            Assert.Equal(ShippedTypes.Task.Hash, (await TypesAsync(client, "beta")).Single(t => t.Name == "task").Hash);

            ParcelView added = await client.PostAsync<ParcelView>(
                "/parcels", new ParcelAddRequest("alpha", "task", "Starts building", "build"), CancellationToken.None);
            Assert.Equal(qa.Hash, added.TypeHash);

            DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.GetAsync<List<TypeView>>("/types?project=_node", CancellationToken.None));
            Assert.Contains("reserved for node-level records", exception.Message, StringComparison.Ordinal);
        });
    }

    private static async Task<List<FlowView>> FlowsAsync(DaemonClient client, string project) =>
        await client.GetAsync<List<FlowView>>($"/flows?project={Uri.EscapeDataString(project)}", CancellationToken.None);

    [Fact]
    public async Task Starting_the_daemon_seeds_the_four_shipped_flows_as_published_shipped_scope_definitions_and_a_second_start_appends_nothing()
    {
        using var temp = new TemporaryHome();

        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);
        int afterFirst = await StreamCountAsync(temp.Home);
        Assert.Equal(ShippedDefinitions.All.Count * 2, afterFirst);

        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await using IQuerySession session = store.QuerySession(Tenants.Node);
            IReadOnlyList<Definition> all = await session.Query<Definition>().ToListAsync(CancellationToken.None);
            List<Definition> flows = [.. all.Where(d => d.Kind == DefinitionKind.Flow)];
            Assert.Equivalent(ShippedFlows.All.Select(f => (f.Name, f.Hash)), flows.Select(d => (d.Name, d.ContentHash)), strict: true);
            Assert.All(flows, d => Assert.True(d.Scope == DefinitionScope.Shipped && d.Status == DefinitionStatus.Published && d.VersionNumber == 1));
            Assert.All(flows, d => Assert.True(Flow.FromJson(d.Content).Hash == d.ContentHash));
        }

        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);
        Assert.Equal(afterFirst, await StreamCountAsync(temp.Home));
    }

    [Fact]
    public async Task The_flow_list_endpoint_returns_exactly_the_four_shipped_flows_on_a_fresh_node_home()
    {
        using var temp = new TemporaryHome();

        await RunAndStopAsync(temp.Home, async client =>
        {
            List<FlowView> flows = await FlowsAsync(client, "alpha");

            Assert.Equal(ShippedFlows.All.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => (f.Name, 1, f.Hash)), flows.Select(f => (f.Name, f.Version, f.Hash)));
            Assert.Equivalent(new[] { "build", "merge", "discovery", "review-on-assignment" }, flows.Select(f => f.Name), strict: true);

            DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.GetAsync<List<FlowView>>("/flows?project=_node", CancellationToken.None));
            Assert.Contains("reserved for node-level records", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_project_flow_overrides_the_shipped_one_in_that_project_only()
    {
        using var temp = new TemporaryHome();
        await RunAndStopAsync(temp.Home, _ => Task.CompletedTask);

        Flow quick = ShippedFlows.ReviewOnAssignment with { Budget = 1m };
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);
            Definition draft = await definitions.DraftAsync(
                Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, "alpha",
                quick.Name, Flow.CanonicalJson(quick), quick.Hash, null, CancellationToken.None);
            await definitions.PublishAsync(Principal.LocalOwner, "alpha", draft.Id, CancellationToken.None);
        }

        await RunAndStopAsync(temp.Home, async client =>
        {
            Assert.Equal(quick.Hash, (await FlowsAsync(client, "alpha")).Single(f => f.Name == "review-on-assignment").Hash);
            Assert.Equal(ShippedFlows.ReviewOnAssignment.Hash, (await FlowsAsync(client, "beta")).Single(f => f.Name == "review-on-assignment").Hash);
        });
    }

    [Fact]
    public void The_flow_list_query_is_open_to_every_role()
    {
        foreach (Role role in Enum.GetValues<Role>())
        {
            Assert.Equal(EntryKind.Query, CommandRegistry.Require("flow.list", new Principal("someone", role)).Kind);
        }
    }

    [Fact]
    public async Task Seeding_a_store_that_holds_the_earlier_shipped_flows_publishes_the_merge_flow_beside_them()
    {
        using var temp = new TemporaryHome();
        ShippedDefinition[] earlier = [.. ShippedFlows.All.Where(c => c.Name != "merge").Select(ShippedDefinition.FromFlow)];
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, [.. ShippedDefinitions.ParcelTypes, .. earlier]);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);

        await definitions.SeedAsync(ShippedDefinitions.All, CancellationToken.None);

        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Definition> all = await session.Query<Definition>().ToListAsync(CancellationToken.None);
        List<Definition> flows = [.. all.Where(d => d.Kind == DefinitionKind.Flow)];
        // the merge flow is published as its first version and the others are left as they were
        Assert.Equivalent(ShippedFlows.All.Select(c => (c.Name, 1, c.Hash)), flows.Select(d => (d.Name, d.VersionNumber, d.ContentHash)), strict: true);
        Assert.All(flows, d => Assert.True(d.Status == DefinitionStatus.Published && d.Scope == DefinitionScope.Shipped));
    }

    [Fact]
    public async Task The_build_flows_new_hash_seeds_as_the_next_version_of_the_one_without_field_declarations()
    {
        using var temp = new TemporaryHome();
        Flow previous = PreviousFlows.BuildWithGatedMerge;
        Flow before = previous with { Steps = [.. previous.Steps.Select(s => s with { Fields = [] })] };
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, [ShippedDefinition.FromFlow(before)]);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);

        IReadOnlyList<Definition> seeded = await definitions.SeedAsync(ShippedDefinitions.All, CancellationToken.None);

        Definition build = seeded.Single(d => d.Kind == DefinitionKind.Flow && d.Name == "build");
        Assert.Equal(2, build.VersionNumber);
        Assert.Equal(ShippedFlows.Build.Hash, build.ContentHash);
    }

    [Fact]
    public async Task The_build_flows_new_hash_seeds_as_the_next_version_of_the_one_without_source_declarations()
    {
        using var temp = new TemporaryHome();
        Flow previous = PreviousFlows.BuildWithGatedMerge;
        Flow before = previous with { Steps = [.. previous.Steps.Select(s => s with { Sources = [] })] };
        Assert.NotEqual(previous.Hash, before.Hash);
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, [ShippedDefinition.FromFlow(before)]);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);

        IReadOnlyList<Definition> seeded = await definitions.SeedAsync(ShippedDefinitions.All, CancellationToken.None);

        Definition build = seeded.Single(d => d.Kind == DefinitionKind.Flow && d.Name == "build");
        Assert.Equal(2, build.VersionNumber);
        Assert.Equal(ShippedFlows.Build.Hash, build.ContentHash);
    }

    [Fact]
    public async Task Seeding_a_store_that_holds_the_build_flow_of_the_gated_merge_publishes_the_one_ending_at_the_open_pull_request_as_the_next_version()
    {
        using var temp = new TemporaryHome();
        Flow previous = PreviousFlows.BuildWithGatedMerge;
        Flow beforeSources = previous with { Steps = [.. previous.Steps.Select(s => s with { Sources = [] })] };
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, [ShippedDefinition.FromFlow(beforeSources)]);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);
        await definitions.SeedAsync([ShippedDefinition.FromFlow(previous)], CancellationToken.None);

        IReadOnlyList<Definition> seeded = await definitions.SeedAsync(ShippedDefinitions.All, CancellationToken.None);

        Definition build = seeded.Single(d => d.Kind == DefinitionKind.Flow && d.Name == "build");
        Assert.Equal(3, build.VersionNumber);
        Assert.Equal(DefinitionStatus.Published, build.Status);
        Assert.Equal(ShippedFlows.Build.Hash, build.ContentHash);
        Assert.NotEqual(previous.Hash, build.ContentHash);
        Assert.DoesNotContain(Flow.FromJson(build.Content).Steps, s => s.Name == "gated-merge");
    }

    [Fact]
    public async Task Seeding_a_store_that_holds_the_merge_flow_before_its_rebase_lap_publishes_the_new_one_as_its_next_version()
    {
        using var temp = new TemporaryHome();
        Flow previous = PreviousFlows.MergeWithoutRebase;
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, [ShippedDefinition.FromFlow(previous)]);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);

        IReadOnlyList<Definition> seeded = await definitions.SeedAsync(ShippedDefinitions.All, CancellationToken.None);

        Definition merge = seeded.Single(d => d.Kind == DefinitionKind.Flow && d.Name == "merge");
        Assert.Equal(2, merge.VersionNumber);
        Assert.Equal(DefinitionStatus.Published, merge.Status);
        Assert.Equal(ShippedFlows.Merge.Hash, merge.ContentHash);
        Assert.NotEqual(previous.Hash, merge.ContentHash);
        Assert.Equal(["gated-merge", "wait-for-checks", "worktree", "rebase"], Flow.FromJson(merge.Content).Steps.Select(s => s.Name));
    }
}
