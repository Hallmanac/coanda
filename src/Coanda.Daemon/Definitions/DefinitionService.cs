using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Definitions;

/// <summary>
/// The daemon's side of every definition command: pass the registry, ask the decider, append,
/// save. Project-scope definitions live in their project's tenant; owner-scope and shipped-scope
/// definitions live in the node tenant (<see cref="Tenants.Node"/>). Writes are serialized so that
/// two drafts of one name cannot be handed the same version. A drafted parcel type is validated
/// against the names of the gates the daemon runs, and a drafted or seeded contour against the
/// names of the actions it registered; none of either when it is given none.
/// </summary>
public sealed class DefinitionService(IDocumentStore store, TimeProvider clock, GateSet? gates = null, IReadOnlySet<string>? actions = null)
{
    /// <summary>
    /// The principal seeding acts as: the daemon itself, in the Owner role, because seeding is the
    /// publication of the definitions the binary carries.
    /// </summary>
    public static readonly Principal SeedPrincipal = new("coanda-seed", Role.Owner);

    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly DefinitionResolver resolver = new(store);

    public Task<Definition> DraftParcelTypeAsync(
        Principal principal, DefinitionScope scope, string? project, ParcelType type, ForkBase? forkBase, CancellationToken cancellationToken) =>
        DraftAsync(principal, DefinitionKind.ParcelType, scope, project, type.Name, ParcelType.CanonicalJson(type), type.Hash, forkBase, cancellationToken);

    public async Task<Definition> DraftAsync(
        Principal principal,
        DefinitionKind kind,
        DefinitionScope scope,
        string? project,
        string name,
        string content,
        string contentHash,
        ForkBase? forkBase,
        CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.draft", principal);
        string tenant = TenantOf(scope, project);

        await writes.WaitAsync(cancellationToken);
        try
        {
            await using IDocumentSession session = store.LightweightSession(tenant);
            IReadOnlyList<Definition> existing = await session.Query<Definition>().ToListAsync(cancellationToken);
            int latest = LatestVersion(existing, kind, scope, project, name);
            DefinitionDrafted drafted = DefinitionDecider.Draft(
                kind, name, scope, project, latest, content, contentHash, forkBase, principal.Id, clock.GetUtcNow(), (gates ?? GateSet.None).Names, actions);

            session.Events.StartStream<Definition>(drafted.DefinitionId, drafted);
            await session.SaveChangesAsync(cancellationToken);
            return Definition.Create(drafted);
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>Publishes a draft. A null project names the node tenant, where owner-scope drafts live.</summary>
    public async Task<Definition> PublishAsync(Principal principal, string? project, Guid definitionId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.publish", principal);

        await using IDocumentSession session = store.LightweightSession(TenantOf(project));
        var stream = await session.Events.FetchForWriting<Definition>(definitionId, cancellationToken);
        Definition definition = stream.Aggregate ?? throw NotFound(definitionId, project);
        DefinitionPublished published = DefinitionDecider.Publish(definition, principal.Id, clock.GetUtcNow());
        stream.AppendOne(published);
        await session.SaveChangesAsync(cancellationToken);

        definition.Apply(published);
        return definition;
    }

    /// <summary>Retires a published definition. A null project names the node tenant.</summary>
    public async Task<Definition> RetireAsync(Principal principal, string? project, Guid definitionId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.retire", principal);

        await using IDocumentSession session = store.LightweightSession(TenantOf(project));
        var stream = await session.Events.FetchForWriting<Definition>(definitionId, cancellationToken);
        Definition definition = stream.Aggregate ?? throw NotFound(definitionId, project);
        DefinitionRetired retired = DefinitionDecider.Retire(definition, principal.Id, clock.GetUtcNow());
        stream.AppendOne(retired);
        await session.SaveChangesAsync(cancellationToken);

        definition.Apply(retired);
        return definition;
    }

    /// <summary>The agent definitions that resolution returns for the project, ordered by name.</summary>
    public async Task<IReadOnlyList<Definition>> ListAgentsAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("agent.list", principal);
        return await resolver.ResolveAllAsync(Tenants.RequireProject(project), DefinitionKind.Agent, cancellationToken);
    }

    /// <summary>The published agent that resolution returns for the project by name, or null when it returns none.</summary>
    public async Task<Definition?> ResolveAgentAsync(Principal principal, string project, string name, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("agent.list", principal);
        return await resolver.ResolveAsync(Tenants.RequireProject(project), DefinitionKind.Agent, name, cancellationToken);
    }

    /// <summary>
    /// Seeds the shipped definitions, whatever their kind, as published shipped-scope definitions.
    /// A definition whose content hash is already among the shipped versions of its name is
    /// skipped, so a second run over the same store appends nothing; a new hash becomes the next
    /// version. Returns the definitions it seeded.
    /// </summary>
    public async Task<IReadOnlyList<Definition>> SeedAsync(IReadOnlyCollection<ShippedDefinition> shipped, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.publish", SeedPrincipal);

        await writes.WaitAsync(cancellationToken);
        try
        {
            await using IDocumentSession session = store.LightweightSession(Tenants.Node);
            IReadOnlyList<Definition> inNode = await session.Query<Definition>().ToListAsync(cancellationToken);
            List<Definition> known = [.. inNode.Where(d => d.Scope == DefinitionScope.Shipped)];
            List<Definition> seeded = [];

            foreach (ShippedDefinition definition in shipped)
            {
                List<Definition> versions = [.. known.Where(d => d.Kind == definition.Kind && DefinitionResolution.SameName(d.Name, definition.Name))];
                if (versions.Any(d => string.Equals(d.ContentHash, definition.ContentHash, StringComparison.Ordinal)))
                {
                    continue;
                }

                (DefinitionDrafted drafted, DefinitionPublished published) = DefinitionDecider.Seed(
                    definition, versions.Select(d => d.VersionNumber).DefaultIfEmpty(0).Max(), SeedPrincipal.Id, clock.GetUtcNow(), actions);
                session.Events.StartStream<Definition>(drafted.DefinitionId, drafted, published);

                Definition created = Definition.Create(drafted);
                created.Apply(published);
                known.Add(created);
                seeded.Add(created);
            }

            await session.SaveChangesAsync(cancellationToken);
            return seeded;
        }
        finally
        {
            writes.Release();
        }
    }

    private static int LatestVersion(IEnumerable<Definition> existing, DefinitionKind kind, DefinitionScope scope, string? project, string name) =>
        existing
            .Where(d => d.Kind == kind
                && d.Scope == scope
                && string.Equals(d.Project, project?.Trim(), StringComparison.Ordinal)
                && DefinitionResolution.SameName(d.Name, name.Trim()))
            .Select(d => d.VersionNumber)
            .DefaultIfEmpty(0)
            .Max();

    private static string TenantOf(DefinitionScope scope, string? project) => scope switch
    {
        DefinitionScope.Project => Tenants.RequireProject(project ?? string.Empty),
        _ => Tenants.Node,
    };

    private static string TenantOf(string? project) =>
        project is null ? Tenants.Node : Tenants.RequireProject(project);

    private static DomainException NotFound(Guid definitionId, string? project) =>
        new($"No definition {Ids.Short(definitionId)} in {(project is null ? "the node's definitions" : $"project '{project}'")}.");
}
