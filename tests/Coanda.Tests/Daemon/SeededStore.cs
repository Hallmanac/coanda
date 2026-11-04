using Coanda.Core.Definitions;
using Coanda.Core.Software;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Opens a store the way the daemon leaves it: the shipped parcel types seeded. Parcel types come
/// from the store, so a test that opens one without starting the daemon seeds it first.
/// </summary>
public static class SeededStore
{
    public static Task<IDocumentStore> OpenAsync(string databasePath) =>
        OpenAsync(databasePath, ShippedDefinitions.ParcelTypes);

    /// <summary>Opens a store seeded with <paramref name="shipped"/>; a flow run test passes <see cref="ShippedDefinitions.All"/> so the flows resolve.</summary>
    public static async Task<IDocumentStore> OpenAsync(string databasePath, IReadOnlyCollection<ShippedDefinition> shipped)
    {
        IDocumentStore store = StoreConfiguration.Open(databasePath);
        await new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names).SeedAsync(shipped, CancellationToken.None);
        return store;
    }
}
