using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Modules;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Triggers;
using Fisher;
using JasperFx.MultiTenancy;
using JasperFx.Events.Projections;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // Every other store service Fisher registers resolves this one, so the daemon's store, which the
        // container disposes, is a home store.
        services.Replace(ServiceDescriptor.Singleton<DocumentStore, HomeStore>(provider => new HomeStore(provider.GetRequiredService<StoreOptions>())));
        return services;
    }

    public static IDocumentStore Open(string databasePath)
    {
        var options = new StoreOptions();
        Configure(options, databasePath);
        return new HomeStore(options);
    }

    public static void Configure(StoreOptions options, string databasePath)
    {
        options.Connection(ConnectionStringOf(databasePath));
        options.Events.TenancyStyle = TenancyStyle.Conjoined;
        options.Policies.AllDocumentsAreMultiTenanted();
        foreach (InlineSnapshot snapshot in InlineSnapshots)
        {
            snapshot.Register(options);
        }

        EarlierRecords.Configure(options);
    }

    /// <summary>The connection string every connection the store opens over <paramref name="databasePath"/> is opened with.</summary>
    /// <remarks>
    /// Unpooled, so each connection the store opens is its own native connection and closing it closes that
    /// native connection. Microsoft.Data.Sqlite 10.0.9's pool can hand one native connection to two holders:
    /// handing one out, it marks the connection active outside the pool's lock and only then records the
    /// holder, and an open that meanwhile reclaims leaked connections takes it for one whose holder was
    /// collected. Two holders of one native connection refuse each other's function registration while a
    /// statement steps (SQLite Error 5) and roll back each other's transactions when a Fisher session closes.
    /// </remarks>
    public static string ConnectionStringOf(string databasePath) =>
        new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ConnectionString;

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
