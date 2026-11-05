using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Projects;

/// <summary>
/// The daemon's side of every project command and query: pass the registry, check the name the way
/// every project command does, ask the decider, append, save. A project's record lives in its own
/// tenant, and so do the modules switched on for it; the credential profile it names is a node-local
/// setting in the node tenant. Writes are
/// serialized so that two concurrent adds of one name cannot both pass the duplicate check, and two
/// settings of one project's profile cannot both start its stream.
/// </summary>
public sealed class ProjectService(IDocumentStore store, TimeProvider clock, CredentialProfiles profiles)
{
    private readonly SemaphoreSlim writes = new(1, 1);

    /// <summary>A project service whose daemon holds no credential profiles.</summary>
    public ProjectService(IDocumentStore store, TimeProvider clock)
        : this(store, clock, new CredentialProfiles([]))
    {
    }

    /// <summary>The rule a command quotes when the project it names has no record.</summary>
    public static string Unregistered(string project) => ProjectDecider.Unregistered(project);

    /// <summary>Registers a project whose gates run <see cref="Project.DefaultBuild"/> and <see cref="Project.DefaultTest"/>.</summary>
    public Task<Project> AddAsync(Principal principal, string name, string remote, string primaryBranch, CancellationToken cancellationToken) =>
        AddAsync(principal, name, remote, primaryBranch, buildCommand: null, testCommand: null, cancellationToken);

    /// <summary>Registers a project; a null build or test command leaves its gates on the default.</summary>
    public async Task<Project> AddAsync(
        Principal principal,
        string name,
        string remote,
        string primaryBranch,
        string? buildCommand,
        string? testCommand,
        CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.add", principal);
        string tenant = Tenants.RequireProject(name);

        await writes.WaitAsync(cancellationToken);
        try
        {
            Project? existing = await FindAsync(store, tenant, cancellationToken);
            IReadOnlyList<string> registeredNames = existing is null ? await NamesAsync(store, cancellationToken) : [];
            ProjectRegistered registered = ProjectDecider.Register(
                tenant, remote, primaryBranch, existing, principal.Id, clock.GetUtcNow(), buildCommand, testCommand, registeredNames);

            await using IDocumentSession session = store.LightweightSession(tenant);
            session.Events.StartStream<Project>(registered.ProjectId, registered);
            await session.SaveChangesAsync(cancellationToken);
            return Project.Create(registered);
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>
    /// Replaces the checks a merge of the project's pull requests waits for with <paramref name="checks"/>,
    /// recorded exactly as given in the project's own record.
    /// </summary>
    public async Task<Project> SetRequiredChecksAsync(
        Principal principal, string name, IReadOnlyList<string> checks, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.set", principal);
        string tenant = Tenants.RequireProject(name);

        await writes.WaitAsync(cancellationToken);
        try
        {
            Project? registered = await FindAsync(store, tenant, cancellationToken);
            ProjectRequiredChecksSet set = ProjectDecider.SetRequiredChecks(tenant, registered, checks, principal.Id, clock.GetUtcNow());

            await using IDocumentSession session = store.LightweightSession(tenant);
            var stream = await session.Events.FetchForWriting<Project>(set.ProjectId, cancellationToken);
            Project project = stream.Aggregate ?? throw new DomainException(ProjectDecider.Unregistered(tenant));
            stream.AppendOne(set);
            await session.SaveChangesAsync(cancellationToken);

            project.Apply(set);
            return project;
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>
    /// Names the credential profile the project's agent steps spawn with, replacing any it named.
    /// The name must be one the running daemon's configuration holds. Recorded in the node tenant.
    /// </summary>
    public async Task<ProjectProfile> SetProfileAsync(Principal principal, string name, string profile, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.set-profile", principal);
        string tenant = Tenants.RequireProject(name);

        await writes.WaitAsync(cancellationToken);
        try
        {
            Project? registered = await FindAsync(store, tenant, cancellationToken);
            ProjectProfile? existing = await FindProfileAsync(store, tenant, cancellationToken);
            ProjectProfileSet set = ProjectProfileDecider.Set(
                tenant, registered, existing, profile, profiles.Names, principal.Id, clock.GetUtcNow());

            await using IDocumentSession session = store.LightweightSession(Tenants.Node);
            if (existing is null)
            {
                session.Events.StartStream<ProjectProfile>(set.SettingId, set);
            }
            else
            {
                session.Events.Append(set.SettingId, set);
            }

            await session.SaveChangesAsync(cancellationToken);
            return ProjectProfile.Create(set);
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>The name of the credential profile the project names, or null when it names none.</summary>
    public async Task<string?> ProfileOfAsync(Principal principal, string name, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.show", principal);
        string tenant = Tenants.RequireProject(name);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        ProjectProfile? setting = await FindProfileAsync(store, tenant, cancellationToken);
        return setting?.Profile;
    }

    /// <summary>
    /// The credential profile every project that names one names, keyed by project; for a Module
    /// principal, only those of the projects its module is switched on for.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ProfilesAsync(Principal principal, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.list", principal);

        IReadOnlyList<ProjectProfile> settings;
        await using (IQuerySession session = store.QuerySession(Tenants.Node))
        {
            settings = await session.Query<ProjectProfile>().ToListAsync(cancellationToken);
        }

        IReadOnlySet<string>? visible = principal.Role == Role.Module
            ? (await VisibleAsync(principal, cancellationToken)).Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : null;
        return settings
            .Where(s => visible is null || visible.Contains(s.Project))
            .ToDictionary(s => s.Project, s => s.Profile, StringComparer.Ordinal);
    }

    /// <summary>The project's record, or null when the name has none.</summary>
    public async Task<Project?> ShowAsync(Principal principal, string name, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.show", principal);
        string tenant = Tenants.RequireProject(name);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        return await FindAsync(store, tenant, cancellationToken);
    }

    /// <summary>
    /// Every project registered on the node, ordered by name with ordinal comparison; for a Module
    /// principal, every project its module is switched on for.
    /// </summary>
    public async Task<IReadOnlyList<Project>> ListAsync(Principal principal, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.list", principal);
        IReadOnlyList<Project> projects = await VisibleAsync(principal, cancellationToken);
        return [.. projects.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Replaces the set of modules switched on for the project with <paramref name="modules"/>, recorded
    /// in the project's own record, so the set replicates with the project.
    /// </summary>
    public async Task<Project> SetModulesAsync(
        Principal principal, string name, IReadOnlyList<string> modules, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.set-modules", principal);
        string tenant = Tenants.RequireProject(name);

        await writes.WaitAsync(cancellationToken);
        try
        {
            Project? registered = await FindAsync(store, tenant, cancellationToken);
            ProjectModulesSet set = ProjectDecider.SetModules(tenant, registered, modules, principal.Id, clock.GetUtcNow());

            await using IDocumentSession session = store.LightweightSession(tenant);
            var stream = await session.Events.FetchForWriting<Project>(set.ProjectId, cancellationToken);
            Project project = stream.Aggregate ?? throw new DomainException(ProjectDecider.Unregistered(tenant));
            stream.AppendOne(set);
            await session.SaveChangesAsync(cancellationToken);

            project.Apply(set);
            return project;
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>
    /// Every project registered on the node that <paramref name="principal"/> may act in: all of them,
    /// or for a Module principal those its module is switched on for. Read without a registry check.
    /// </summary>
    private async Task<IReadOnlyList<Project>> VisibleAsync(Principal principal, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Project> projects = await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        return [.. projects.Where(p => ModuleScope.IsSwitchedOn(principal, p))];
    }

    /// <summary>The name of every project registered on the node, read without a registry check.</summary>
    private static async Task<IReadOnlyList<string>> NamesAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Project> projects = await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        return [.. projects.Select(p => p.Name)];
    }

    /// <summary>
    /// The record held in <paramref name="tenant"/>, a name <see cref="Tenants.RequireProject"/> has
    /// already returned, or null when it holds none. Read without a registry check, for a service
    /// that has passed its own.
    /// </summary>
    internal static async Task<Project?> FindAsync(IDocumentStore store, string tenant, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Project> found = await session.Query<Project>().ToListAsync(cancellationToken);
        return found.Count == 0 ? null : found[0];
    }

    /// <summary>
    /// The profile setting of the project <paramref name="tenant"/> names, a name
    /// <see cref="Tenants.RequireProject"/> has already returned, read from the node tenant, or null
    /// when it names none. Read without a registry check, for a caller that has passed its own.
    /// </summary>
    private static async Task<ProjectProfile?> FindProfileAsync(IDocumentStore store, string tenant, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<ProjectProfile> found = await session.Query<ProjectProfile>().Where(p => p.Project == tenant).ToListAsync(cancellationToken);
        return found.Count == 0 ? null : found[0];
    }
}
