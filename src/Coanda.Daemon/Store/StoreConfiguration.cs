using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
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
        options.Projections.Snapshot<Parcel>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<Definition>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<Flow>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<ExternalEvent>(SnapshotLifecycle.Inline);
        options.Projections.Snapshot<Subscription>(SnapshotLifecycle.Inline);
    }
}
