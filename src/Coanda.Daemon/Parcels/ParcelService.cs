using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Parcels;

/// <summary>
/// The daemon's side of every parcel command and query: open a session for the project (the
/// tenant), ask the decider, append, save. The registry check is the caller's first line. A parcel
/// is created only in a project that has a record. A new parcel's type is whatever
/// <see cref="DefinitionResolver"/> resolves for its project; an existing parcel keeps the version
/// whose hash it recorded. Every move runs the gates its type binds to it, from the daemon's gates,
/// the shipped gate set when it is given none.
/// </summary>
public sealed class ParcelService(IDocumentStore store, TimeProvider clock, GateSet? gates = null)
{
    private readonly DefinitionResolver definitions = new(store);
    private readonly GateSet gates = gates ?? GateSet.Shipped;

    public Task<Parcel> AddAsync(Principal principal, string project, string typeName, string title, string? state, CancellationToken cancellationToken) =>
        AddAsync(principal, project, typeName, title, state, origin: null, cancellationToken);

    /// <summary>Creates a parcel recording <paramref name="origin"/>, what created it when a person's add did not.</summary>
    public async Task<Parcel> AddAsync(
        Principal principal, string project, string typeName, string title, string? state, string? origin, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.add", principal);
        string tenant = Tenants.RequireProject(project);
        await RequireRegisteredAsync(tenant, cancellationToken);
        ParcelType type = await RequireTypeAsync(tenant, typeName, cancellationToken);
        ParcelCreated created = ParcelDecider.Create(type, gates, tenant, title, state, principal.Id, clock.GetUtcNow(), origin);

        await using IDocumentSession session = store.LightweightSession(created.Project);
        session.Events.StartStream<Parcel>(created.ParcelId, created);
        await session.SaveChangesAsync(cancellationToken);

        return Parcel.Create(created);
    }

    /// <summary>A move made on nobody's flow's behalf, as a person's move through the API is.</summary>
    public Task<Parcel> MoveAsync(Principal principal, string project, Guid parcelId, string to, CancellationToken cancellationToken) =>
        MoveAsync(principal, project, parcelId, to, forFlow: null, cancellationToken);

    /// <summary>
    /// Moves the parcel on behalf of <paramref name="forFlow"/>, or of no flow when it is null. The flow is
    /// named explicitly and never read from the principal's id: a parcel a live flow holds moves only for that flow.
    /// </summary>
    public async Task<Parcel> MoveAsync(
        Principal principal, string project, Guid parcelId, string to, Guid? forFlow, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.move", principal);
        string tenant = Tenants.RequireProject(project);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var stream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = stream.Aggregate ?? throw NotFound(parcelId, project);
        ParcelType type = await RequireCreatedUnderTypeAsync(tenant, parcel, cancellationToken);
        ParcelMoved moved = ParcelDecider.Move(type, gates, parcel, to, principal.Id, clock.GetUtcNow(), forFlow);
        stream.AppendOne(moved);
        await session.SaveChangesAsync(cancellationToken);

        parcel.Apply(moved);
        return parcel;
    }

    /// <summary>Records a value in one of the parcel's fields, replacing the value it held, under the type version the parcel was created under.</summary>
    public async Task<Parcel> SetFieldAsync(
        Principal principal, string project, Guid parcelId, string field, FieldValue value, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.set-field", principal);
        string tenant = Tenants.RequireProject(project);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var stream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = stream.Aggregate ?? throw NotFound(parcelId, project);
        ParcelType type = await RequireCreatedUnderTypeAsync(tenant, parcel, cancellationToken);
        ParcelFieldSet set = ParcelDecider.SetField(type, parcel, field, value, principal.Id, clock.GetUtcNow());
        stream.AppendOne(set);
        await session.SaveChangesAsync(cancellationToken);

        parcel.Apply(set);
        return parcel;
    }

    public async Task<Parcel> CloseAsync(Principal principal, string project, Guid parcelId, string resolution, string reason, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.close", principal);
        string tenant = Tenants.RequireProject(project);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var stream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = stream.Aggregate ?? throw NotFound(parcelId, project);
        ParcelType type = await RequireCreatedUnderTypeAsync(tenant, parcel, cancellationToken);
        ParcelClosed closed = ParcelDecider.Close(type, gates, parcel, resolution, reason, principal.Id, clock.GetUtcNow());
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

    /// <summary>
    /// The parcel as it stood at <paramref name="version"/> of its stream, folded from its events up to
    /// and including that version, or null when the project holds no such parcel.
    /// </summary>
    public async Task<Parcel?> ShowAtAsync(Principal principal, string project, Guid parcelId, long version, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.show", principal);
        string tenant = Tenants.RequireProject(project);

        await using IQuerySession session = store.QuerySession(tenant);
        return await session.Events.AggregateStreamAsync<Parcel>(parcelId, version, token: cancellationToken);
    }

    /// <summary>The parcel in the project whose creation recorded <paramref name="origin"/>, or null when there is none.</summary>
    public async Task<Parcel?> FindByOriginAsync(Principal principal, string project, string origin, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.list", principal);
        string tenant = Tenants.RequireProject(project);

        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Parcel> found = await session.Query<Parcel>().Where(p => p.Origin == origin).ToListAsync(cancellationToken);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>The parcel type that resolution returns for the project by name, or null when it returns none.</summary>
    public async Task<ParcelType?> ResolveTypeAsync(Principal principal, string project, string name, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("type.list", principal);
        return await definitions.ResolveParcelTypeAsync(Tenants.RequireProject(project), name, cancellationToken);
    }

    /// <summary>The version of a parcel type that the project holds by its hash, or null when it holds none; a parcel reads its fields from the version it was created under.</summary>
    public async Task<ParcelType?> TypeVersionAsync(Principal principal, string project, string typeHash, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("parcel.show", principal);
        return await definitions.ResolveParcelTypeByHashAsync(Tenants.RequireProject(project), typeHash, cancellationToken);
    }

    /// <summary>The parcel types that resolution returns for the project, ordered by name.</summary>
    public async Task<IReadOnlyList<ParcelType>> TypesAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("type.list", principal);
        return await definitions.ResolveParcelTypesAsync(Tenants.RequireProject(project), cancellationToken);
    }

    /// <summary>A parcel is created only in a registered project, so the software actions have a repository to act on.</summary>
    private async Task RequireRegisteredAsync(string project, CancellationToken cancellationToken)
    {
        if (await ProjectService.FindAsync(store, project, cancellationToken) is null)
        {
            throw new DomainException(ProjectService.Unregistered(project));
        }
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

    /// <summary>A parcel keeps the type version it was created under; a newer version applies to new parcels only.</summary>
    private async Task<ParcelType> RequireCreatedUnderTypeAsync(string project, Parcel parcel, CancellationToken cancellationToken)
    {
        ParcelType? type = await definitions.ResolveParcelTypeByHashAsync(project, parcel.TypeHash, cancellationToken);
        return type
            ?? throw new DomainException($"The '{parcel.TypeName}' type version {parcel.TypeHash} that parcel {Ids.Short(parcel.Id)} was created under is no longer held.");
    }

    private static DomainException NotFound(Guid parcelId, string project) =>
        new($"No parcel {Ids.Short(parcelId)} in project '{project}'.");
}
