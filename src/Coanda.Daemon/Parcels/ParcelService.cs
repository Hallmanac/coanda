using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Parcels;

/// <summary>
/// The daemon's side of every parcel command and query: open a session for the project (the
/// tenant), ask the decider, append, save. The registry check is the caller's first line. A
/// parcel's type is whatever <see cref="DefinitionResolver"/> returns for its project.
/// </summary>
public sealed class ParcelService(IDocumentStore store, TimeProvider clock)
{
    private readonly DefinitionResolver definitions = new(store);

    public async Task<Parcel> AddAsync(Principal principal, string project, string typeName, string title, string? state, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.add", principal);
        string tenant = Tenants.RequireProject(project);
        ParcelType type = await RequireTypeAsync(tenant, typeName, cancellationToken);
        ParcelCreated created = ParcelDecider.Create(type, tenant, title, state, principal.Id, clock.GetUtcNow());

        await using IDocumentSession session = store.LightweightSession(created.Project);
        session.Events.StartStream<Parcel>(created.ParcelId, created);
        await session.SaveChangesAsync(cancellationToken);

        return Parcel.Create(created);
    }

    public async Task<Parcel> MoveAsync(Principal principal, string project, Guid parcelId, string to, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.move", principal);
        string tenant = Tenants.RequireProject(project);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var stream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = stream.Aggregate ?? throw NotFound(parcelId, project);
        ParcelType type = await RequireTypeAsync(tenant, parcel.TypeName, cancellationToken);
        ParcelMoved moved = ParcelDecider.Move(type, parcel, to, principal.Id, clock.GetUtcNow());
        stream.AppendOne(moved);
        await session.SaveChangesAsync(cancellationToken);

        parcel.Apply(moved);
        return parcel;
    }

    public async Task<Parcel> CloseAsync(Principal principal, string project, Guid parcelId, string resolution, string reason, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.close", principal);
        string tenant = Tenants.RequireProject(project);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var stream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = stream.Aggregate ?? throw NotFound(parcelId, project);
        ParcelType type = await RequireTypeAsync(tenant, parcel.TypeName, cancellationToken);
        ParcelClosed closed = ParcelDecider.Close(type, parcel, resolution, reason, principal.Id, clock.GetUtcNow());
        stream.AppendOne(closed);
        await session.SaveChangesAsync(cancellationToken);

        parcel.Apply(closed);
        return parcel;
    }

    public async Task<IReadOnlyList<Parcel>> ListAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.list", principal);
        string tenant = Tenants.RequireProject(project);

        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Parcel> parcels = await session.Query<Parcel>().OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        return parcels;
    }

    public async Task<Parcel?> ShowAsync(Principal principal, string project, Guid parcelId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.show", principal);
        string tenant = Tenants.RequireProject(project);

        await using IQuerySession session = store.QuerySession(tenant);
        return await session.LoadAsync<Parcel>(parcelId, cancellationToken);
    }

    /// <summary>The parcel types that resolution returns for the project, ordered by name.</summary>
    public async Task<IReadOnlyList<ParcelType>> TypesAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("type.list", principal);
        return await definitions.ResolveParcelTypesAsync(Tenants.RequireProject(project), cancellationToken);
    }

    private async Task<ParcelType> RequireTypeAsync(string project, string name, CancellationToken cancellationToken)
    {
        ParcelType? type = await definitions.ResolveParcelTypeAsync(project, name, cancellationToken);
        if (type is not null)
        {
            return type;
        }

        IReadOnlyList<ParcelType> installed = await definitions.ResolveParcelTypesAsync(project, cancellationToken);
        throw new DomainException($"'{name}' is not an installed parcel type; installed: {string.Join(", ", installed.Select(t => t.Name))}.");
    }

    private static DomainException NotFound(Guid parcelId, string project) =>
        new($"No parcel {Ids.Short(parcelId)} in project '{project}'.");
}
