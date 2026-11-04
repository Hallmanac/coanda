using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Projects;

/// <summary>
/// The daemon's side of every project command and query: pass the registry, check the name the way
/// every project command does, ask the decider, append, save. A project's record lives in its own
/// tenant; the credential profile it names is a node-local setting in the node tenant. Writes are
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
            ProjectRegistered registered = ProjectDecider.Register(
                tenant, remote, primaryBranch, existing, principal.Id, clock.GetUtcNow(), buildCommand, testCommand);

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
    /// Names the credential profile the project's judgments spawn with, replacing any it named.
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
        ProjectProfile? setting = await FindProfileAsync(store, Tenants.RequireProject(name), cancellationToken);
        return setting?.Profile;
    }

    /// <summary>The credential profile every project that names one names, keyed by project.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ProfilesAsync(Principal principal, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.list", principal);

        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<ProjectProfile> settings = await session.Query<ProjectProfile>().ToListAsync(cancellationToken);
        return settings.ToDictionary(s => s.Project, s => s.Profile, StringComparer.Ordinal);
    }

    /// <summary>The project's record, or null when the name has none.</summary>
    public async Task<Project?> ShowAsync(Principal principal, string name, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.show", principal);
        return await FindAsync(store, Tenants.RequireProject(name), cancellationToken);
    }

    /// <summary>Every project registered on the node, ordered by name with ordinal comparison.</summary>
    public async Task<IReadOnlyList<Project>> ListAsync(Principal principal, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project.list", principal);

        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<Project> projects = await session.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
        return [.. projects.OrderBy(p => p.Name, StringComparer.Ordinal)];
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
