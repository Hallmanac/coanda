using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Types;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Coanda.Tests.Sdk;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A node home main wrote at e86428f, the commit this change started from, begins and reads normally:
/// its definitions were recorded in shipped scope, and they read as module-scope definitions of the
/// software module at version 0.1.0 (<see cref="EarlierShippedRecords"/>), resolve as they did, and
/// lose nothing. The fixture is that home's database as <c>sqlite3 .dump</c> wrote it. Main seeded it
/// over a store holding the build flow as the last build before the step prefix shipped it, so the
/// build flow is at version 2; then it registered alpha, published in alpha a fork of build version 2
/// with a budget of 30, published in owner scope a fork of fix version 1 whose model's effort is max,
/// and added a task in alpha.
/// </summary>
public sealed class StoreWrittenByMainTests
{
    private const string Fixture = "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-e86428f.sql";

    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly Flow AlphaBuild = SoftwareFlows.Build with { Budget = 30m };

    private static readonly AgentDefinition OwnerFix = SoftwareAgents.Fix with { Model = SoftwareAgents.Fix.Model with { Effort = "max" } };

    private static void Restore(NodeHome home)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = home.DatabasePath, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand restore = connection.CreateCommand();
        restore.CommandText = File.ReadAllText(RepositoryFiles.PathOf(Fixture));
        restore.ExecuteNonQuery();
    }

    private static async Task<List<(DefinitionKind Kind, string Name, string Hash)>> ResolvedAsync(IDocumentStore store, string project)
    {
        var resolver = new DefinitionResolver(store);
        List<(DefinitionKind, string, string)> resolved = [];
        foreach (DefinitionKind kind in Enum.GetValues<DefinitionKind>())
        {
            resolved.AddRange((await resolver.ResolveAllAsync(project, kind, None)).Select(d => (d.Kind, d.Name, d.ContentHash)));
        }

        return resolved;
    }

    private static List<(DefinitionKind Kind, string Name, string Hash)> Expected(Flow build) =>
    [
        .. SoftwareTypes.All.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => (DefinitionKind.ParcelType, t.Name, t.Hash)),
        .. SoftwareFlows.All.Select(f => f.Name == "build" ? build : f).OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => (DefinitionKind.Flow, f.Name, f.Hash)),
        .. SoftwareAgents.All.Select(a => a.Name == "fix" ? OwnerFix : a).OrderBy(a => a.Name, StringComparer.Ordinal).Select(a => (DefinitionKind.Agent, a.Name, a.Hash)),
    ];

    private static async Task<List<Definition>> HeldAsync(IDocumentStore store)
    {
        List<Definition> held = [];
        foreach (string tenant in new[] { Tenants.Node, "alpha" })
        {
            await using IQuerySession session = store.QuerySession(tenant);
            held.AddRange(await session.Query<Definition>().ToListAsync(None));
        }

        return held;
    }

    private static async Task<int> DefinitionEventsAsync(IDocumentStore store, IEnumerable<Definition> definitions)
    {
        int events = 0;
        foreach (Definition definition in definitions)
        {
            await using IQuerySession session = store.QuerySession(definition.Project ?? Tenants.Node);
            events += (await session.Events.FetchStreamAsync(definition.Id, token: None)).Count;
        }

        return events;
    }

    [Fact]
    public async Task A_node_home_main_wrote_starts_and_every_project_resolves_the_definitions_it_did_with_none_lost()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        List<Definition> before;
        int eventsBefore;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            before = await HeldAsync(store);
            eventsBefore = await DefinitionEventsAsync(store, before);
        }

        Assert.Equal(14, before.Count);

        await using (RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None))
        {
            await daemon.StopAsync(None);
        }

        await using IDocumentStore after = StoreConfiguration.Open(temp.Home.DatabasePath);
        Assert.Equal(Expected(AlphaBuild), await ResolvedAsync(after, "alpha"));
        Assert.Equal(Expected(SoftwareFlows.Build), await ResolvedAsync(after, "beta"));

        // Only the task type was imported, as its next version, since it has gained the repos field since
        // main wrote the home; main's records already hold every other hash the module's files have, and
        // every record main wrote is still there as it was.
        List<Definition> held = await HeldAsync(after);
        Definition imported = Assert.Single(held, d => before.All(b => b.Id != d.Id));
        Assert.Equal(
            (DefinitionKind.ParcelType, "task", DefinitionScope.Module, SoftwareTypes.Task.Hash, DefinitionStatus.Published),
            (imported.Kind, imported.Name, imported.Scope, imported.ContentHash, imported.Status));
        List<Definition> kept = [.. held.Where(d => d.Id != imported.Id)];
        Assert.Equal(eventsBefore, await DefinitionEventsAsync(after, kept));
        Assert.Equivalent(
            before.Select(d => (d.Id, d.Kind, d.Name, d.Scope, d.Project, d.VersionNumber, d.ContentHash, d.Status, d.ForkBase)),
            kept.Select(d => (d.Id, d.Kind, d.Name, d.Scope, d.Project, d.VersionNumber, d.ContentHash, d.Status, d.ForkBase)),
            strict: true);

        // What main recorded in shipped scope reads as the software module's, at version 0.1.0.
        List<Definition> earlier = [.. kept.Where(d => d.Scope == DefinitionScope.Module)];
        Assert.Equal(12, earlier.Count);
        Assert.All(earlier, d => Assert.Equal(("software", "0.1.0"), (d.Module, d.ModuleVersion)));
        Assert.All(kept.Where(d => d.Scope != DefinitionScope.Module), d => Assert.Equal((null, null), (d.Module, d.ModuleVersion)));
        Assert.Equal(["build", "build"], earlier.Where(d => d.Kind == DefinitionKind.Flow && d.Name == "build").OrderBy(d => d.VersionNumber).Select(d => Flow.FromJson(d.Content).Name));
    }

    [Fact]
    public async Task A_fork_main_recorded_against_a_shipped_definition_resolves_its_base_to_the_same_content_in_module_scope()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        await using (RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None))
        {
            await daemon.StopAsync(None);
        }

        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var resolver = new DefinitionResolver(store);
        Definition alphaBuild = await resolver.ResolveAsync("alpha", DefinitionKind.Flow, "build", None) ?? throw new InvalidOperationException("No build flow in alpha.");
        Definition ownerFix = await resolver.ResolveAsync("alpha", DefinitionKind.Agent, "fix", None) ?? throw new InvalidOperationException("No fix agent in alpha.");

        Assert.Equal((DefinitionScope.Project, ForkBase.Earlier("build", 2), AlphaBuild.Hash), (alphaBuild.Scope, alphaBuild.ForkBase, alphaBuild.ContentHash));
        Definition buildBase = await resolver.ForkedFromAsync(alphaBuild, None) ?? throw new InvalidOperationException("The fork's base resolves to nothing.");
        Assert.Equal((DefinitionScope.Module, 2, SoftwareFlows.Build.Hash, "software", "0.1.0"), (buildBase.Scope, buildBase.VersionNumber, buildBase.ContentHash, buildBase.Module, buildBase.ModuleVersion));
        // The fixture's text was written on macOS and CanonicalJson writes the platform's line ending,
        // so the content is compared as the flow it reads as rather than as text.
        Assert.Equal(Flow.CanonicalJson(SoftwareFlows.Build), Flow.CanonicalJson(Flow.FromJson(buildBase.Content)));

        Assert.Equal((DefinitionScope.Owner, ForkBase.Earlier("fix", 1), OwnerFix.Hash), (ownerFix.Scope, ownerFix.ForkBase, ownerFix.ContentHash));
        Definition fixBase = await resolver.ForkedFromAsync(ownerFix, None) ?? throw new InvalidOperationException("The fork's base resolves to nothing.");
        Assert.Equal((SoftwareAgents.Fix.Hash, "software", "0.1.0"), (fixBase.ContentHash, fixBase.Module, fixBase.ModuleVersion));
    }

    [Fact]
    public async Task A_parcel_main_created_still_reads_the_type_it_was_created_under()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        await using (RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None))
        {
            await daemon.StopAsync(None);
        }

        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        await using IQuerySession session = store.QuerySession("alpha");
        Parcel parcel = Assert.Single(await session.Query<Parcel>().ToListAsync(None));
        ParcelType? createdUnder = await new DefinitionResolver(store).ResolveParcelTypeByHashAsync("alpha", parcel.TypeHash, None);

        // Created under the task type main had, before the repos field, which it still reads.
        Assert.Equal(SoftwareTypes.UnownedTask.Hash, parcel.TypeHash);
        Assert.Equal(SoftwareTypes.UnownedTask.Hash, createdUnder?.Hash);
    }
}
