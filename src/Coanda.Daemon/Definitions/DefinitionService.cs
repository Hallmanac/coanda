using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.FlowRuns;
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
        SessionScope.RequireProjectScope(principal, scope, name);
        string tenant = TenantOf(scope, project);
        if (scope == DefinitionScope.Project)
        {
            SessionScope.RequireBoundProject(principal, tenant);
        }

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

    /// <summary>
    /// Drafts the definition of <paramref name="kind"/> that <paramref name="json"/>, a file in the shape
    /// a module ships for that kind, holds: read through the kind's canonical read, as a module's import
    /// reads it, so the content and hash recorded are the import's whatever the file's layout, and the
    /// caller gives no hash. <paramref name="project"/> is read only in project scope. When
    /// <paramref name="fork"/> is set, the draft's fork base is the module-scope definition of its kind
    /// and name this node holds, the highest version of it when it holds more than one, and the draft is
    /// refused when it holds none.
    /// </summary>
    public async Task<Definition> DraftAsync(
        Principal principal,
        DefinitionKind kind,
        DefinitionScope scope,
        string? project,
        string json,
        bool fork,
        CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.draft", principal);
        ModuleDefinition read = ModuleDefinition.Read(kind, json);
        ForkBase? forkBase = fork ? await ForkBaseOfAsync(kind, read.Name, cancellationToken) : null;
        return await DraftAsync(
            principal, kind, scope, scope == DefinitionScope.Project ? project : null, read.Name, read.Content, read.ContentHash, forkBase, cancellationToken);
    }

    /// <summary>
    /// Every definition of every kind and status that can govern the project: its own, and the
    /// owner-scope and module-scope ones, drafts and retired versions included.
    /// </summary>
    public async Task<IReadOnlyList<Definition>> ListAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.list", principal);
        return await GoverningAsync(principal, project, cancellationToken);
    }

    /// <summary>The definition <paramref name="definitionId"/> among those that can govern the project, or null when it is none of them.</summary>
    public async Task<Definition?> ShowAsync(Principal principal, string project, Guid definitionId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.show", principal);
        IReadOnlyList<Definition> governing = await GoverningAsync(principal, project, cancellationToken);
        return governing.FirstOrDefault(d => d.Id == definitionId);
    }

    /// <summary>
    /// Publishes the draft <paramref name="definitionId"/>, one of the definitions that can govern
    /// <paramref name="project"/>, in the tenant that holds it.
    /// </summary>
    public async Task<Definition> PublishInProjectAsync(Principal principal, string project, Guid definitionId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.publish", principal);
        Definition found = await GoverningOneAsync(principal, project, definitionId, cancellationToken);
        return await PublishAsync(principal, HoldingProject(found), definitionId, cancellationToken);
    }

    /// <summary>
    /// Retires the published definition <paramref name="definitionId"/>, one of the definitions that can
    /// govern <paramref name="project"/>, in the tenant that holds it.
    /// </summary>
    public async Task<Definition> RetireInProjectAsync(Principal principal, string project, Guid definitionId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("definition.retire", principal);
        Definition found = await GoverningOneAsync(principal, project, definitionId, cancellationToken);
        return await RetireAsync(principal, HoldingProject(found), definitionId, cancellationToken);
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
        SessionScope.RequireBoundProject(principal, tenant);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        return await resolver.ResolveAllAsync(tenant, DefinitionKind.Agent, cancellationToken);
    }

    /// <summary>The published agent that resolution returns for the project by name, or null when it returns none.</summary>
    public async Task<Definition?> ResolveAgentAsync(Principal principal, string project, string name, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("agent.list", principal);
        string tenant = Tenants.RequireProject(project);
        SessionScope.RequireBoundProject(principal, tenant);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        return await resolver.ResolveAsync(tenant, DefinitionKind.Agent, name, cancellationToken);
    }

    /// <summary>
    /// The agent definition, in the project, owner, or module scope, whose content hash a launch
    /// recorded, retired since or not; null when the store holds none with that hash.
    /// </summary>
    public async Task<Definition?> ResolveAgentByHashAsync(Principal principal, string project, string contentHash, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("agent.list", principal);
        string tenant = Tenants.RequireProject(project);
        SessionScope.RequireBoundProject(principal, tenant);
        return await resolver.ResolveByHashAsync(tenant, DefinitionKind.Agent, contentHash, cancellationToken);
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

    /// <summary>
    /// The fork base naming the module-scope definition of <paramref name="kind"/> and
    /// <paramref name="name"/> this node holds, the highest version of it, which is the one resolution
    /// picks within module scope; refused when the node holds none.
    /// </summary>
    private async Task<ForkBase> ForkBaseOfAsync(DefinitionKind kind, string name, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Definition> moduleVersions = await ModuleVersionsAsync(kind, cancellationToken);
        Definition? highest = moduleVersions
            .Where(d => DefinitionResolution.SameName(d.Name, name.Trim()))
            .MaxBy(d => d.VersionNumber);
        return highest is null
            ? throw new DomainException(
                $"No module-scope {DefinitionDecider.Describe(kind)} named '{name.Trim()}' is held on this node, so a draft of it cannot fork one; a fork names the module-scope definition of the draft's own kind and name.")
            : ForkBase.Of(highest);
    }

    private async Task<IReadOnlyList<Definition>> GoverningAsync(Principal principal, string project, CancellationToken cancellationToken)
    {
        string tenant = Tenants.RequireProject(project);
        SessionScope.RequireBoundProject(principal, tenant);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        return await resolver.GoverningAsync(tenant, cancellationToken);
    }

    private async Task<Definition> GoverningOneAsync(Principal principal, string project, Guid definitionId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Definition> governing = await GoverningAsync(principal, project, cancellationToken);
        return governing.FirstOrDefault(d => d.Id == definitionId)
            ?? throw new DomainException($"No definition {Ids.Short(definitionId)} can govern project '{project}'.");
    }

    /// <summary>The project whose tenant holds <paramref name="definition"/>: its own in project scope, and none, the node tenant, otherwise.</summary>
    private static string? HoldingProject(Definition definition) =>
        definition.Scope == DefinitionScope.Project ? definition.Project : null;

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
