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
    public static Task<IDocumentStore> OpenAsync(string databasePath) =>
        OpenAsync(databasePath, ShippedDefinition.ShippedParcelTypes);

    /// <summary>Opens a store seeded with <paramref name="shipped"/>; a flow test passes <see cref="ShippedDefinition.All"/> so the contours resolve.</summary>
    public static async Task<IDocumentStore> OpenAsync(string databasePath, IReadOnlyCollection<ShippedDefinition> shipped)
    {
        IDocumentStore store = StoreConfiguration.Open(databasePath);
        await new DefinitionService(store, TimeProvider.System).SeedAsync(shipped, CancellationToken.None);
        return store;
    }
}
