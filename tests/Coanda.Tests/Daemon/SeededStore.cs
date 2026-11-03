using Coanda.Core.Definitions;
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
    public static async Task<IDocumentStore> OpenAsync(string databasePath)
    {
        IDocumentStore store = StoreConfiguration.Open(databasePath);
        await new DefinitionService(store, TimeProvider.System).SeedAsync(ShippedDefinition.ShippedParcelTypes, CancellationToken.None);
        return store;
    }
}
