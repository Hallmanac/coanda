using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A fork base drafted on one node names the same base on a node whose store imported the module's
/// definitions in a different order or at a different module version, because it names the base by
/// name, module, and content hash rather than by the version number one store gave it.
/// </summary>
public sealed class ForkBaseResolutionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A project's fork of the task type the module brings now, which keeps the repos field it owns.</summary>
    private static readonly ParcelType Fork = SoftwareTypes.Task with { Resolutions = [.. SoftwareTypes.Task.Resolutions, "spiked"] };

    /// <summary>A store that imported the software module's task type at each of <paramref name="imports"/>, in order.</summary>
    private static async Task<IDocumentStore> ImportedAsync(TemporaryHome home, params (string ModuleVersion, ParcelType Type)[] imports)
    {
        IDocumentStore store = StoreConfiguration.Open(home.Home.DatabasePath);
        DefinitionService definitions = Definitions(store);
        foreach ((string moduleVersion, ParcelType type) in imports)
        {
            await definitions.ImportAsync(ModuleArtifact.Of("software", moduleVersion, [ModuleDefinition.FromParcelType(type)]), None);
        }

        return store;
    }

    private static DefinitionService Definitions(IDocumentStore store) => new(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);

    private static async Task<Definition> HeldAsync(IDocumentStore store, string contentHash)
    {
        await using IQuerySession node = store.QuerySession(Tenants.Node);
        IReadOnlyList<Definition> held = await node.Query<Definition>().ToListAsync(None);
        return held.Single(d => d.Scope == DefinitionScope.Module && d.ContentHash == contentHash);
    }

    /// <summary>Drafts <paramref name="fork"/> in alpha on <paramref name="store"/>, forked from the module-scope task type with <paramref name="baseHash"/>.</summary>
    private static async Task<Definition> DraftForkAsync(IDocumentStore store, string baseHash, ParcelType fork)
    {
        ForkBase forkBase = ForkBase.Of(await HeldAsync(store, baseHash));
        return await Definitions(store).DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, "alpha", fork, forkBase, None);
    }

    private static async Task<Definition> ForkedFromAsync(IDocumentStore store, Definition fork) =>
        await new DefinitionResolver(store).ForkedFromAsync(fork, None) ?? throw new InvalidOperationException("The fork's base resolves to nothing.");

    [Fact]
    public async Task A_fork_base_resolves_to_its_content_on_two_stores_that_number_that_content_differently()
    {
        using var upgraded = new TemporaryHome();
        using var fresh = new TemporaryHome();
        await using IDocumentStore first = await ImportedAsync(upgraded, ("0.1.0", SoftwareTypes.UnownedTask), ("0.1.0", SoftwareTypes.Task));
        await using IDocumentStore second = await ImportedAsync(fresh, ("0.1.0", SoftwareTypes.Task));

        Definition fork = await DraftForkAsync(first, SoftwareTypes.Task.Hash, Fork);

        Assert.Equal(new ForkBase("task", "software", "0.1.0", SoftwareTypes.Task.Hash), fork.ForkBase);
        Definition onFirst = await ForkedFromAsync(first, fork);
        Definition onSecond = await ForkedFromAsync(second, fork);
        Assert.Equal((2, SoftwareTypes.Task.Hash), (onFirst.VersionNumber, onFirst.ContentHash));
        Assert.Equal((1, SoftwareTypes.Task.Hash), (onSecond.VersionNumber, onSecond.ContentHash));
    }

    [Fact]
    public async Task A_fork_base_resolves_on_a_store_that_holds_its_unchanged_content_under_a_later_module_version()
    {
        using var stepped = new TemporaryHome();
        using var direct = new TemporaryHome();
        await using IDocumentStore first = await ImportedAsync(stepped, ("0.1.0", SoftwareTypes.Task), ("0.2.0", SoftwareTypes.Task));
        await using IDocumentStore second = await ImportedAsync(direct, ("0.2.0", SoftwareTypes.Task));

        Definition fork = await DraftForkAsync(first, SoftwareTypes.Task.Hash, Fork);

        // The first store kept the unchanged content under the module version it first imported it at.
        Assert.Equal("0.1.0", fork.ForkBase?.ModuleVersion);
        Definition onSecond = await ForkedFromAsync(second, fork);
        Assert.Equal((SoftwareTypes.Task.Hash, "0.2.0"), (onSecond.ContentHash, onSecond.ModuleVersion));
    }

    [Fact]
    public async Task A_fork_base_names_one_of_two_contents_a_store_holds_under_one_name_module_and_module_version()
    {
        using var upgraded = new TemporaryHome();
        await using IDocumentStore store = await ImportedAsync(upgraded, ("0.1.0", SoftwareTypes.UnownedTask), ("0.1.0", SoftwareTypes.Task));
        ParcelType earlierFork = SoftwareTypes.UnownedTask with { Resolutions = [.. SoftwareTypes.UnownedTask.Resolutions, "spiked"] };

        Definition fork = await DraftForkAsync(store, SoftwareTypes.UnownedTask.Hash, earlierFork);

        Definition forkedFrom = await ForkedFromAsync(store, fork);
        Assert.Equal((1, SoftwareTypes.UnownedTask.Hash, "0.1.0"), (forkedFrom.VersionNumber, forkedFrom.ContentHash, forkedFrom.ModuleVersion));
    }

    [Fact]
    public async Task A_fork_base_reads_back_from_the_store_in_its_shape_with_no_store_given_version_number()
    {
        using var home = new TemporaryHome();
        Definition fork;
        await using (IDocumentStore store = await ImportedAsync(home, ("0.1.0", SoftwareTypes.Task)))
        {
            fork = await DraftForkAsync(store, SoftwareTypes.Task.Hash, Fork);
        }

        await using IDocumentStore reopened = StoreConfiguration.Open(home.Home.DatabasePath);
        await using IQuerySession alpha = reopened.QuerySession("alpha");
        Definition? read = await alpha.LoadAsync<Definition>(fork.Id, None);
        Assert.Equal(new ForkBase("task", "software", "0.1.0", SoftwareTypes.Task.Hash), read?.ForkBase);
        Assert.Null(read?.ForkBase?.Version);
    }
}
