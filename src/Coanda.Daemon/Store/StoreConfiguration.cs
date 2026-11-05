using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Modules;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Triggers;
using Fisher;
using JasperFx.MultiTenancy;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Daemon.Store;

public static class StoreConfiguration
{
    /// <summary>
    /// One SQLite file per node; the project is the tenant on every stream and document, so a
    /// project is a filter rather than a file. The daemon is the only writer.
    /// </summary>
    public static IServiceCollection AddCoandaStore(this IServiceCollection services, NodeHome home)
    {
        services.AddFisher(options => Configure(options, home.DatabasePath))
            .ApplyAllDatabaseChangesOnStartup();
        return services;
    }

    public static IDocumentStore Open(string databasePath) =>
        DocumentStore.For(options => Configure(options, databasePath));

    public static void Configure(StoreOptions options, string databasePath)
    {
        options.Connection($"Data Source={databasePath}");
        options.Events.TenancyStyle = TenancyStyle.Conjoined;
        options.Policies.AllDocumentsAreMultiTenanted();
        foreach (InlineSnapshot snapshot in InlineSnapshots)
        {
            snapshot.Register(options);
        }

        EarlierRecords.Configure(options);
    }

    /// <summary>
    /// The documents the store keeps as inline snapshots of their streams, regenerated whenever an event
    /// is appended to one: every other document the store holds is written by the daemon itself and is
    /// not derived from the events.
    /// </summary>
    public static IReadOnlyList<InlineSnapshot> InlineSnapshots { get; } =
    [
        InlineSnapshot.Of<Project>(),
        InlineSnapshot.Of<ProjectProfile>(),
        InlineSnapshot.Of<ModuleSettings>(),
        InlineSnapshot.Of<ModuleProfileSelection>(),
        InlineSnapshot.Of<Parcel>(),
        InlineSnapshot.Of<Definition>(),
        InlineSnapshot.Of<FlowRun>(),
        InlineSnapshot.Of<ExternalEvent>(),
        InlineSnapshot.Of<Subscription>(),
    ];

    /// <summary>The document table the store keeps documents of <paramref name="type"/> in.</summary>
    public static string DocumentTableOf(Type type) => $"fi_doc_{type.Name.ToLowerInvariant()}";
}

/// <summary>
/// One document type the store snapshots inline: its type, how the store is told of it, and how its
/// document for one stream is regenerated from that stream's events.
/// </summary>
public sealed record InlineSnapshot(
    Type Type,
    Action<StoreOptions> Register,
    Func<IDocumentStore, Guid, string, CancellationToken, Task> RebuildStreamAsync)
{
    /// <summary>The name a stream of this document's aggregate is recorded under in the streams table.</summary>
    public string StreamType => Type.Name;

    public static InlineSnapshot Of<T>()
        where T : class =>
        new(
            typeof(T),
            options => options.Projections.Snapshot<T>(SnapshotLifecycle.Inline),
            (store, stream, tenant, cancellationToken) => store.Advanced.RebuildSingleStreamAsync<T>(stream, tenant, cancellationToken));
}
