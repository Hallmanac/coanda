using Coanda.Core.Definitions;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Coanda.Tests.Core;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Opens a store the way the daemon leaves it: the shipped parcel types seeded. Parcel types come
/// from the store, so a test that opens one without starting the daemon seeds it first. A flow it seeds
/// may name the software steps as this build names them or as builds before the prefix did.
/// </summary>
public static class SeededStore
{
    public static Task<IDocumentStore> OpenAsync(string databasePath) =>
        OpenAsync(databasePath, SoftwareDefinitions.ParcelTypes);

    /// <summary>Opens a store seeded with <paramref name="shipped"/>; a flow run test passes <see cref="SoftwareDefinitions.All"/> so the flows resolve.</summary>
    public static async Task<IDocumentStore> OpenAsync(string databasePath, IReadOnlyCollection<ModuleDefinition> shipped)
    {
        IDocumentStore store = StoreConfiguration.Open(databasePath);
        await new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps).ImportAsync(SoftwareDefinitions.Artifact(shipped), CancellationToken.None);
        return store;
    }
}
