using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Definitions;

/// <summary>
/// The daemon's side of every definition command: pass the registry, ask the decider, append,
/// save. Project-scope definitions live in their project's tenant; owner-scope and module-scope
/// definitions live in the node tenant (<see cref="Tenants.Node"/>). Writes are serialized so that
/// two drafts of one name cannot be handed the same version. A drafted parcel type is validated
/// against the names of the gates the daemon runs, and a drafted or imported flow against the system
/// step names <see cref="SystemStepNames"/> gives for the definition's project, or for no project when
/// the definition belongs to none (an owner-scope or module-scope one); the shipped gate set when it
/// is given no gates, and no system steps when it is given none. A drafted or imported flow's system
/// steps' arguments are checked, for the same project, against what <see cref="StepArgumentChecks"/>
/// gives, the argument schemas of the modules that provide them; unchecked when it is given none.
/// </summary>
public sealed class DefinitionService(
    IDocumentStore store,
    TimeProvider clock,
    GateSet? gates,
    DefinitionService.SystemStepNames? systemSteps,
    DefinitionService.StepArgumentChecks? stepArguments = null)
{
    /// <summary>The system step names a flow of <paramref name="project"/>, or of no project when it is null, may name.</summary>
    public delegate Task<IReadOnlySet<string>> SystemStepNames(string? project, CancellationToken cancellationToken);

    /// <summary>What the argument schemas of the steps a flow of <paramref name="project"/>, or of no project when it is null, may name say of a step's arguments.</summary>
    public delegate Task<StepArgumentCheck> StepArgumentChecks(string? project, CancellationToken cancellationToken);

    /// <summary>A definition service whose flows may name the system steps <paramref name="systemSteps"/>, whatever their project.</summary>
    public DefinitionService(IDocumentStore store, TimeProvider clock, GateSet? gates = null, IReadOnlySet<string>? systemSteps = null)
        : this(store, clock, gates, systemSteps is null ? null : (_, _) => Task.FromResult(systemSteps))
    {
    }

    /// <summary>
    /// The principal a module's import acts as: the daemon itself, in the Owner role, because importing
    /// is the publication of the definitions a module the node installed brings.
    /// </summary>
    public static readonly Principal ImportPrincipal = new("coanda-import", Role.Owner);

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
            IReadOnlyCollection<Definition> moduleVersions = await ModuleVersionsAsync(kind, cancellationToken);
            string? flowProject = scope == DefinitionScope.Project ? project : null;
            IReadOnlySet<string>? known = kind == DefinitionKind.Flow && systemSteps is not null
                ? await systemSteps(flowProject, cancellationToken)
                : null;
            StepArgumentCheck? check = kind == DefinitionKind.Flow && stepArguments is not null
                ? await stepArguments(flowProject, cancellationToken)
                : null;
            DefinitionDrafted drafted = DefinitionDecider.Draft(
                kind, name, scope, project, latest, content, contentHash, forkBase, principal.Id, clock.GetUtcNow(),
                (gates ?? GateSet.Shipped).Names, known, moduleVersions, check);

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
        string tenant = Tenants.RequireProject(project);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        return await resolver.ResolveAllAsync(tenant, DefinitionKind.Agent, cancellationToken);
    }

    /// <summary>The published agent that resolution returns for the project by name, or null when it returns none.</summary>
    public async Task<Definition?> ResolveAgentAsync(Principal principal, string project, string name, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("agent.list", principal);
        string tenant = Tenants.RequireProject(project);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        return await resolver.ResolveAsync(tenant, DefinitionKind.Agent, name, cancellationToken);
    }

    /// <summary>
    /// Imports a module's definitions, whatever their kind, as published module-scope definitions
    /// carrying the module's name and version, in one save: when any of them breaks a rule, nothing
    /// is recorded, and the refusal names the file it came from. A definition whose content hash is
    /// already among the module-scope versions of its kind and name is skipped, so a second import of
    /// the same definitions records nothing and an earlier version's content is never recorded again;
    /// a new hash becomes the next version; and a kind and name another module's module-scope
    /// definition holds is refused. Returns the definitions it recorded.
    /// </summary>
    public async Task<IReadOnlyList<Definition>> ImportAsync(ModuleArtifact module, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.publish", ImportPrincipal);
        module.RequireOnePerName();

        await writes.WaitAsync(cancellationToken);
        try
        {
            await using IDocumentSession session = store.LightweightSession(Tenants.Node);
            IReadOnlyList<Definition> inNode = await session.Query<Definition>().ToListAsync(cancellationToken);
            List<Definition> known = [.. inNode.Where(d => d.Scope == DefinitionScope.Module)];
            List<Definition> imported = [];
            IReadOnlySet<string>? stepNames = systemSteps is null ? null : await systemSteps(null, cancellationToken);
            StepArgumentCheck? check = stepArguments is null ? null : await stepArguments(null, cancellationToken);

            foreach (ModuleArtifactDefinition item in module.Definitions)
            {
                (DefinitionDrafted Drafted, DefinitionPublished Published)? recorded;
                try
                {
                    recorded = DefinitionDecider.Import(
                        item.Definition, module.Module, module.Version, known, ImportPrincipal.Id, clock.GetUtcNow(), (gates ?? GateSet.Shipped).Names, stepNames, check);
                }
                catch (DomainException broken)
                {
                    throw module.Refusal(item, broken);
                }

                if (recorded is not { } events)
                {
                    continue;
                }

                session.Events.StartStream<Definition>(events.Drafted.DefinitionId, events.Drafted, events.Published);
                Definition created = Definition.Create(events.Drafted);
                created.Apply(events.Published);
                known.Add(created);
                imported.Add(created);
            }

            await session.SaveChangesAsync(cancellationToken);
            return imported;
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>
    /// Every module-scope definition of a kind, which a fork base names one of. Module-scope
    /// definitions live in the node tenant whatever the draft's scope, so a project-scope draft reads
    /// them from there.
    /// </summary>
    private async Task<IReadOnlyCollection<Definition>> ModuleVersionsAsync(DefinitionKind kind, CancellationToken cancellationToken)
    {
        await using IQuerySession node = store.QuerySession(Tenants.Node);
        IReadOnlyList<Definition> inNode = await node.Query<Definition>().ToListAsync(cancellationToken);
        return [.. inNode.Where(d => d.Scope == DefinitionScope.Module && d.Kind == kind)];
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
