using Coanda.Core.Definitions;
using Coanda.Core.Types;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Definitions;

/// <summary>
/// Reads the published definitions that govern a project: its own, from the project's tenant, and
/// the owner-scope and module-scope ones, from the node tenant no project name reaches. The choice among
/// them is <see cref="DefinitionResolution"/>.
/// </summary>
public sealed class DefinitionResolver(IDocumentStore store)
{
    public async Task<Definition?> ResolveAsync(string project, DefinitionKind kind, string name, CancellationToken cancellationToken)
    {
        IReadOnlyList<Definition> published = await PublishedAsync(project, kind, cancellationToken);
        return DefinitionResolution.Resolve(published, project, kind, name);
    }

    public async Task<IReadOnlyList<Definition>> ResolveAllAsync(string project, DefinitionKind kind, CancellationToken cancellationToken)
    {
        IReadOnlyList<Definition> published = await PublishedAsync(project, kind, cancellationToken);
        return DefinitionResolution.ResolveAll(published, project, kind);
    }

    public async Task<ParcelType?> ResolveParcelTypeAsync(string project, string name, CancellationToken cancellationToken)
    {
        Definition? definition = await ResolveAsync(project, DefinitionKind.ParcelType, name, cancellationToken);
        return definition is null ? null : ParcelType.FromJson(definition.Content);
    }

    /// <summary>
    /// The parcel type a parcel was created under: the definition whose content hash the parcel
    /// recorded (see <see cref="ResolveByHashAsync"/>). Null when no held definition has that hash.
    /// </summary>
    public async Task<ParcelType?> ResolveParcelTypeByHashAsync(string project, string contentHash, CancellationToken cancellationToken)
    {
        Definition? found = await ResolveByHashAsync(project, DefinitionKind.ParcelType, contentHash, cancellationToken);
        return found is null ? null : ParcelType.FromJson(found.Content);
    }

    /// <summary>
    /// The definition of the kind, in this project, owner, or module scope, whose content hash a
    /// record kept: a parcel its type's, a flow run its flow's. A version retired since is still
    /// returned, so a record keeps the rules it began under; a draft never is, as nothing began
    /// under one. Null when no held definition has that hash.
    /// </summary>
    public async Task<Definition?> ResolveByHashAsync(string project, DefinitionKind kind, string contentHash, CancellationToken cancellationToken)
    {
        Tenants.RequireProject(project);

        IReadOnlyList<Definition> fromProject = await HeldInAsync(project, kind, cancellationToken);
        IReadOnlyList<Definition> fromNode = await HeldInAsync(Tenants.Node, kind, cancellationToken);
        Definition[] visible =
        [
            .. fromProject.Where(d => d.Scope == DefinitionScope.Project && string.Equals(d.Project, project, StringComparison.Ordinal)),
            .. fromNode.Where(d => d.Scope != DefinitionScope.Project),
        ];
        return visible.FirstOrDefault(d => string.Equals(d.ContentHash, contentHash, StringComparison.Ordinal));
    }

    /// <summary>
    /// The module-scope definition of <paramref name="fork"/>'s kind its fork base names: the one with
    /// the fork base's name, module, and content hash. The module version the fork base records is not
    /// matched, because a node records an unchanged content under the first module version it imported
    /// it at, so two nodes can hold the same base under different module versions. A fork base recorded
    /// before it carried a content hash names its base by the version number this node's store gave it,
    /// and resolves by that number, as it always did. Null when the definition has no fork base or the
    /// store holds none it names. No product path calls this yet: it is the read the fork notice will
    /// make, and until then only tests use it, so it stays internal to the daemon.
    /// </summary>
    internal async Task<Definition?> ForkedFromAsync(Definition fork, CancellationToken cancellationToken)
    {
        if (fork.ForkBase is not { } forkBase)
        {
            return null;
        }

        IReadOnlyList<Definition> fromNode = await HeldInAsync(Tenants.Node, fork.Kind, cancellationToken);
        return fromNode.FirstOrDefault(forkBase.IsBaseOf);
    }

    public async Task<IReadOnlyList<ParcelType>> ResolveParcelTypesAsync(string project, CancellationToken cancellationToken)
    {
        IReadOnlyList<Definition> resolved = await ResolveAllAsync(project, DefinitionKind.ParcelType, cancellationToken);
        return [.. resolved.Select(d => ParcelType.FromJson(d.Content))];
    }

    private async Task<IReadOnlyList<Definition>> PublishedAsync(string project, DefinitionKind kind, CancellationToken cancellationToken)
    {
        Tenants.RequireProject(project);

        IReadOnlyList<Definition> fromProject = await PublishedInAsync(project, kind, cancellationToken);
        IReadOnlyList<Definition> fromNode = await PublishedInAsync(Tenants.Node, kind, cancellationToken);
        return [.. fromProject, .. fromNode];
    }

    private async Task<IReadOnlyList<Definition>> PublishedInAsync(string tenant, DefinitionKind kind, CancellationToken cancellationToken)
    {
        IReadOnlyList<Definition> held = await HeldInAsync(tenant, kind, cancellationToken);
        return [.. held.Where(d => d.Status == DefinitionStatus.Published)];
    }

    /// <summary>Every published or retired definition of the kind in the tenant; drafts were never in force.</summary>
    private async Task<IReadOnlyList<Definition>> HeldInAsync(string tenant, DefinitionKind kind, CancellationToken cancellationToken)
    {
        // Filtered here rather than in the query: Fisher's SQLite LINQ does not match a
        // string-stored enum against an enum constant, and a node holds few definitions.
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Definition> all = await session.Query<Definition>().ToListAsync(cancellationToken);
        return [.. all.Where(d => d.Kind == kind && d.Status != DefinitionStatus.Draft)];
    }
}
