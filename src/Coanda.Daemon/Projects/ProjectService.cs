using Coanda.Core;
using Coanda.Core.Modules;
using Coanda.Core.Modules.Events;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Projects;

/// <summary>
/// The daemon's side of every project command and query: pass the registry, check the name the way
/// every project command does, ask the decider, append, save. A project's record lives in its own
/// tenant, and so do the modules switched on for it and their replicated settings; the credential
/// profile it names is a node-local setting in the node tenant. Writes are serialized so that two
/// concurrent adds of one name cannot both pass the duplicate check, and two settings of one project's
/// profile cannot both start its stream.
/// </summary>
public sealed class ProjectService(IDocumentStore store, TimeProvider clock, CredentialProfiles profiles, IModuleSettingsSchemas schemas, RepoClones? clones)
{
    private readonly SemaphoreSlim writes = new(1, 1);

    /// <summary>A project service that makes no clone when it registers a project, for a store opened without the daemon.</summary>
    public ProjectService(IDocumentStore store, TimeProvider clock, CredentialProfiles profiles, IModuleSettingsSchemas schemas)
        : this(store, clock, profiles, schemas, null)
    {
    }

    /// <summary>
    /// A project service whose daemon holds no credential profiles and whose only module settings schema
    /// is the software module's, the module every registration switches on.
    /// </summary>
    public ProjectService(IDocumentStore store, TimeProvider clock)
        : this(
            store,
            clock,
            new CredentialProfiles([]),
            new ModuleSettingsSchemas(new Dictionary<string, SettingsSchema> { [SoftwareSteps.Module] = SoftwareDescribe.Settings }))
    {
    }

    /// <summary>The rule a command quotes when the project it names has no record.</summary>
    public static string Unregistered(string project) => ProjectDecider.Unregistered(project);

    /// <summary>
    /// The principal that switches the default modules on for a project registered before modules
    /// were, and carries an earlier build's settings into a module's: the daemon itself, in the Owner role.
    /// </summary>
    public static readonly Principal UpgradePrincipal = new("coanda-upgrade", Role.Owner);

    /// <summary>
    /// Registers a project with one repo, at <paramref name="remote"/>, switching on for it the modules
    /// the daemon hosts by default (<see cref="HostedModules.Defaults"/>), and records
    /// <paramref name="settings"/>, each a replicated setting of one of those modules, in the same save.
    /// Each setting is decided against its module's settings schema and the switch-on this registration
    /// records, so a setting its schema refuses, one naming a module the registration does not switch on,
    /// and one named twice each register nothing. Once the registration is saved, the core makes the
    /// clone of the project's repo (<see cref="RepoClones.EnsureAsync"/>) before this returns; a clone it
    /// cannot make is logged and leaves the registration as it is.
    /// </summary>
    public async Task<Project> AddAsync(
        Principal principal,
        string name,
        string remote,
        IReadOnlyList<ModuleSettingValue> settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CommandRegistry.Require("project.add", principal);
        string tenant = Tenants.RequireProject(name);

        Project added;
        await writes.WaitAsync(cancellationToken);
        try
        {
            Project? existing = await FindAsync(store, tenant, cancellationToken);
            IReadOnlyList<string> registeredNames = existing is null ? await NamesAsync(store, cancellationToken) : [];
            ProjectRegistered registered = ProjectDecider.Register(tenant, remote, existing, principal.Id, clock.GetUtcNow(), registeredNames);

            Project project = Project.Create(registered);
            ProjectModulesSet switchedOn = ProjectDecider.SetModules(tenant, project, HostedModules.Defaults, principal.Id, registered.RegisteredAt);
            project.Apply(switchedOn);
            IReadOnlyList<(Guid SettingsId, ModuleSettingSet[] Events)> recorded =
                await DecideSettingsAsync(project, settings, principal, registered.RegisteredAt, cancellationToken);

            await using IDocumentSession session = store.LightweightSession(tenant);
            session.Events.StartStream<Project>(registered.ProjectId, registered, switchedOn);
            foreach ((Guid settingsId, ModuleSettingSet[] events) in recorded)
            {
                session.Events.StartStream<ModuleSettings>(settingsId, events);
            }

            await session.SaveChangesAsync(cancellationToken);
            added = project;
        }
        finally
        {
            writes.Release();
        }

        if (clones is not null)
        {
            await clones.EnsureAsync(added, cancellationToken);
        }

        return added;
    }

    /// <summary>
    /// The settings streams a registration starts, one per module named, each with the events that give
    /// its settings their values, decided against the switch-on <paramref name="project"/> already carries.
    /// </summary>
    private async Task<IReadOnlyList<(Guid SettingsId, ModuleSettingSet[] Events)>> DecideSettingsAsync(
        Project project, IReadOnlyList<ModuleSettingValue> settings, Principal principal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<(Guid, ModuleSettingSet[])> streams = [];

        // A request read from the wire can carry a null element, whatever the annotations say.
        foreach (IGrouping<string, ModuleSettingValue> named in settings.Select(s => s ?? throw new DomainException("A registration's setting was given as null.")).GroupBy(s => s.Module, StringComparer.Ordinal))
        {
            string module = ModuleNames.Require(named.Key);
            if (!project.IsSwitchedOn(module))
            {
                throw new DomainException(
                    $"Project '{project.Name}' was given a setting of module '{module}', which its registration does not switch on; a registration records the settings of the modules it switches on, {string.Join(", ", project.Modules.Select(m => $"'{m}'"))}.");
            }

            SettingsSchema schema = schemas.Find(module)
                ?? throw new DomainException(
                    $"This node holds no settings schema for module '{module}', so project '{project.Name}' cannot be registered with its settings; a module declares its settings.");
            if (named.GroupBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            {
                throw new DomainException($"Project '{project.Name}' was given setting '{twice.Key}' of module '{module}' more than once; give each setting once.");
            }

            Guid settingsId = Ids.New();
            List<ModuleSettingSet> events = [];
            foreach (ModuleSettingValue setting in named)
            {
                events.Add(await ModuleSettingsService.DecideAsync(
                    schema, settingsId, project.Name, module, setting.Name, setting.Value, principal.Id, now, cancellationToken));
            }

            streams.Add((settingsId, [.. events]));
        }

        return streams;
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
    /// Switches the modules the daemon hosts by default on for every project registered on the node
    /// before modules were, which is every project whose record has never named its modules, with
    /// nothing configured, so such a project runs the software module's flows as it did. A project whose modules
    /// were named, even as none, is left as it is. The daemon runs it at start.
    /// </summary>
    public async Task SwitchOnDefaultsAsync(CancellationToken cancellationToken)
    {
        await writes.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<Project> projects;
            await using (IQuerySession query = store.QuerySession(Tenants.Node))
            {
                projects = await query.Query<Project>().AnyTenant().ToListAsync(cancellationToken);
            }

            foreach (Project project in projects)
            {
                await using IDocumentSession session = store.LightweightSession(project.Name);
                IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(project.Id, token: cancellationToken);
                if (events.Any(e => e.Data is ProjectModulesSet))
                {
                    continue;
                }

                ProjectModulesSet set = ProjectDecider.SetModules(
                    project.Name, project, [.. project.Modules, .. HostedModules.Defaults], UpgradePrincipal.Id, clock.GetUtcNow());
                session.Events.Append(project.Id, set);
                await session.SaveChangesAsync(cancellationToken);
            }
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
