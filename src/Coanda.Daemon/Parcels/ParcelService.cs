using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Parcels;

/// <summary>
/// The daemon's side of every parcel command and query: open a session for the project (the
/// tenant), ask the decider, append, save. The registry check is the caller's first line.
/// </summary>
public sealed class ParcelService(IDocumentStore store, TimeProvider clock)
{
    public async Task<Parcel> AddAsync(Principal principal, string project, string typeName, string title, string? state, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.add", principal);
        ParcelType type = RequireType(typeName);
        ParcelCreated created = ParcelDecider.Create(type, project, title, state, principal.Id, clock.GetUtcNow());

        await using IDocumentSession session = store.LightweightSession(created.Project);
        session.Events.StartStream<Parcel>(created.ParcelId, created);
        await session.SaveChangesAsync(cancellationToken);

        return Parcel.Create(created);
    }

    public async Task<Parcel> MoveAsync(Principal principal, string project, Guid parcelId, string to, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.move", principal);

        await using IDocumentSession session = store.LightweightSession(project);
        var stream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = stream.Aggregate ?? throw NotFound(parcelId, project);
        ParcelType type = RequireType(parcel.TypeName);
        ParcelMoved moved = ParcelDecider.Move(type, parcel, to, principal.Id, clock.GetUtcNow());
        stream.AppendOne(moved);
        await session.SaveChangesAsync(cancellationToken);

        parcel.Apply(moved);
        return parcel;
    }

    public async Task<Parcel> CloseAsync(Principal principal, string project, Guid parcelId, string resolution, string reason, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.close", principal);

        await using IDocumentSession session = store.LightweightSession(project);
        var stream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = stream.Aggregate ?? throw NotFound(parcelId, project);
        ParcelType type = RequireType(parcel.TypeName);
        ParcelClosed closed = ParcelDecider.Close(type, parcel, resolution, reason, principal.Id, clock.GetUtcNow());
        stream.AppendOne(closed);
        await session.SaveChangesAsync(cancellationToken);

        parcel.Apply(closed);
        return parcel;
    }

    public async Task<IReadOnlyList<Parcel>> ListAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.list", principal);

        await using IQuerySession session = store.QuerySession(project);
        IReadOnlyList<Parcel> parcels = await session.Query<Parcel>().OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        return parcels;
    }

    public async Task<Parcel?> ShowAsync(Principal principal, string project, Guid parcelId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.show", principal);

        await using IQuerySession session = store.QuerySession(project);
        return await session.LoadAsync<Parcel>(parcelId, cancellationToken);
    }

    public static IReadOnlyList<ParcelType> Types(Principal principal)
    {
        CommandRegistry.Require("type.list", principal);
        return ShippedTypes.All;
    }

    private static ParcelType RequireType(string name) =>
        ShippedTypes.Find(name)
        ?? throw new DomainException($"'{name}' is not an installed parcel type; installed: {string.Join(", ", ShippedTypes.All.Select(t => t.Name))}.");

    private static DomainException NotFound(Guid parcelId, string project) =>
        new($"No parcel {Ids.Short(parcelId)} in project '{project}'.");
}
