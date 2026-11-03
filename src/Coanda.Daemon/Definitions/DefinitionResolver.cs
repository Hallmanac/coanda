using Coanda.Core.Definitions;
using Coanda.Core.Types;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Definitions;

/// <summary>
/// Reads the published definitions that govern a project: its own, from the project's tenant, and
/// the owner and shipped ones, from the node tenant no project name reaches. The choice among
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
        // Filtered here rather than in the query: Fisher's SQLite LINQ does not match a
        // string-stored enum against an enum constant, and a node holds few definitions.
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Definition> all = await session.Query<Definition>().ToListAsync(cancellationToken);
        return [.. all.Where(d => d.Kind == kind && d.Status == DefinitionStatus.Published)];
    }
}
