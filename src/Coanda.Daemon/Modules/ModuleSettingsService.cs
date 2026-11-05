using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Modules;
using Coanda.Core.Modules.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Sdk.Contract;
using Fisher;
using JasperFx.Events.Documents;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// A module's per-project settings, in two scopes. Its replicated settings are recorded in the
/// project's own tenant, one named setting at a time, each value checked against the module's settings
/// schema (<see cref="IModuleSettingsSchemas"/>). Its node-local setting is the credential profile
/// selected for it on this node, recorded by name in the node tenant: one of the entries directly
/// inside the module's area of the node home (<see cref="NodeHome.ModuleAreaOf"/>), read when it is
/// needed, since the area can change while the daemon runs. The core never reads a profile's contents.
/// A Module principal sets and shows only its own module's settings, in a project it is switched on
/// for, and never selects a profile, since that changes the identity the module acts as.
/// </summary>
public sealed class ModuleSettingsService(IDocumentStore store, TimeProvider clock, NodeHome home, IModuleSettingsSchemas schemas)
{
    /// <summary>The name the credential profile setting goes by wherever settings are listed by name.</summary>
    public const string CredentialProfileName = "credential-profile";

    private readonly SemaphoreSlim writes = new(1, 1);

    /// <summary>
    /// Records <paramref name="value"/> for the module's replicated setting <paramref name="name"/> in
    /// the project, replacing the value it held, and shows the module's settings as they then stand.
    /// </summary>
    public async Task<ModuleSettingsShown> SetAsync(
        Principal principal, string project, string module, string name, JsonElement value, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project-module.set", principal);
        (string tenant, string moduleName) = await RequireScopeAsync(principal, project, module, cancellationToken);

        await writes.WaitAsync(cancellationToken);
        try
        {
            SettingsSchema schema = await RequireSwitchedOnAsync(tenant, moduleName, cancellationToken);
            ModuleSettings? existing = await FindSettingsAsync(tenant, moduleName, cancellationToken);
            ModuleSettingSet set = await DecideAsync(
                schema, existing?.Id ?? Ids.New(), tenant, moduleName, name, value, principal.Id, clock.GetUtcNow(), cancellationToken);
            await using IDocumentSession session = store.LightweightSession(tenant);
            if (existing is null)
            {
                session.Events.StartStream<ModuleSettings>(set.SettingsId, set);
            }
            else
            {
                session.Events.Append(set.SettingsId, set);
            }

            await session.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            writes.Release();
        }

        return await ShownAsync(tenant, moduleName, cancellationToken);
    }

    /// <summary>
    /// Selects for the module, in the project on this node, the credential profile <paramref name="profile"/>,
    /// which names an entry directly inside the module's area, replacing any selected before, and shows the
    /// module's settings as they then stand. Only the profile's name is recorded, in the node tenant.
    /// </summary>
    public async Task<ModuleSettingsShown> SelectProfileAsync(
        Principal principal, string project, string module, string profile, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project-module.select-profile", principal);
        (string tenant, string moduleName) = await RequireScopeAsync(principal, project, module, cancellationToken);

        await writes.WaitAsync(cancellationToken);
        try
        {
            SettingsSchema schema = await RequireSwitchedOnAsync(tenant, moduleName, cancellationToken);
            if (schema.CredentialProfile is null)
            {
                throw new DomainException($"Module '{moduleName}' declares no credential profile setting, so no profile is selected for it.");
            }

            if (!Flow.IsPlainName(profile))
            {
                throw new DomainException(
                    $"A credential profile's name is a plain name (letters, digits, hyphens, and underscores); '{profile}' is not.");
            }

            IReadOnlyList<string> held = HeldProfiles(moduleName);
            if (!held.Contains(profile, StringComparer.Ordinal))
            {
                throw new DomainException(
                    $"This node holds no credential profile named '{profile}' for module '{moduleName}'; the profiles it holds for the module are the entries directly inside {home.ModuleAreaOf(moduleName)}, and it "
                    + (held.Count == 0 ? "holds none." : $"holds {string.Join(", ", held.Select(p => $"'{p}'"))}."));
            }

            ModuleProfileSelection? existing = await FindSelectionAsync(tenant, moduleName, cancellationToken);
            var selected = new ModuleProfileSelected(existing?.Id ?? Ids.New(), tenant, moduleName, profile, principal.Id, clock.GetUtcNow());
            await using IDocumentSession session = store.LightweightSession(Tenants.Node);
            if (existing is null)
            {
                session.Events.StartStream<ModuleProfileSelection>(selected.SelectionId, selected);
            }
            else
            {
                session.Events.Append(selected.SelectionId, selected);
            }

            await session.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            writes.Release();
        }

        return await ShownAsync(tenant, moduleName, cancellationToken);
    }

    /// <summary>
    /// The module's settings in the project: each replicated setting it declares with its value, its
    /// default, or neither; its credential profile setting; and its configured state on this node.
    /// </summary>
    public async Task<ModuleSettingsShown> ShowAsync(Principal principal, string project, string module, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("project-module.show", principal);
        (string tenant, string moduleName) = await RequireScopeAsync(principal, project, module, cancellationToken);
        await RequireSwitchedOnAsync(tenant, moduleName, cancellationToken);
        return await ShownAsync(tenant, moduleName, cancellationToken);
    }

    /// <summary>
    /// Whether the module, switched on for the project, is configured on this node or on and
    /// unconfigured, with the names of the settings it requires that have no value and no default. It
    /// is unconfigured when, and only when, there is one. The call daemon code reads the state through;
    /// it passes no registry check, since no caller's request reaches it.
    /// </summary>
    public async Task<ModuleConfiguration> StateAsync(string project, string module, CancellationToken cancellationToken)
    {
        string tenant = Tenants.RequireProject(project);
        string moduleName = ModuleNames.Require(module);
        await RequireSwitchedOnAsync(tenant, moduleName, cancellationToken);
        return (await ShownAsync(tenant, moduleName, cancellationToken)).Configuration;
    }

    /// <summary>
    /// The project and module named, checked, after refusing a Module principal outside a project its
    /// module is switched on for, or acting for another module.
    /// </summary>
    private async Task<(string Tenant, string Module)> RequireScopeAsync(
        Principal principal, string project, string module, CancellationToken cancellationToken)
    {
        string tenant = Tenants.RequireProject(project);
        string moduleName = ModuleNames.Require(module);
        await ModuleScope.RequireSwitchedOnAsync(store, principal, tenant, cancellationToken);
        if (principal.Role == Role.Module && !string.Equals(principal.Module, moduleName, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Module '{principal.Module}' sets and shows only its own settings, not those of module '{moduleName}'.");
        }

        return (tenant, moduleName);
    }

    /// <summary>The module's settings schema, once the project has a record and the module is switched on for it and has a schema on this node.</summary>
    private async Task<SettingsSchema> RequireSwitchedOnAsync(string tenant, string module, CancellationToken cancellationToken)
    {
        Project registered = await ProjectService.FindAsync(store, tenant, cancellationToken)
            ?? throw new DomainException(ProjectService.Unregistered(tenant));
        if (!registered.IsSwitchedOn(module))
        {
            throw new DomainException(
                $"Module '{module}' is not switched on for project '{tenant}'; a module's settings are set and shown only in a project it is switched on for.");
        }

        return schemas.Find(module)
            ?? throw new DomainException(
                $"This node holds no settings schema for module '{module}', so it has no settings this node can set or show; a module declares its settings.");
    }

    /// <summary>
    /// The event that gives the module's replicated setting <paramref name="name"/> the value
    /// <paramref name="value"/> in the project <paramref name="tenant"/>, under the settings stream
    /// <paramref name="settingsId"/>, once the module's settings schema <paramref name="schema"/> declares
    /// the setting and the value meets its schema; otherwise a <see cref="DomainException"/> saying which
    /// fails. It records nothing and checks neither the project nor the module's switch-on, which the
    /// caller has decided.
    /// </summary>
    internal static async Task<ModuleSettingSet> DecideAsync(
        SettingsSchema schema,
        Guid settingsId,
        string tenant,
        string module,
        string name,
        JsonElement value,
        string setBy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ReplicatedSetting declared = schema.Replicated.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
            ?? throw new DomainException(
                $"Module '{module}' declares no replicated setting named '{name}'; it declares "
                + (schema.Replicated.Count == 0 ? "none." : $"{string.Join(", ", schema.Replicated.Select(s => $"'{s.Name}'"))}."));
        string text = await RequireValidAsync(module, declared, value, cancellationToken);
        return new ModuleSettingSet(settingsId, tenant, module, declared.Name, text, setBy, now);
    }

    /// <summary>The value as the JSON text recorded for it, once it meets the setting's schema.</summary>
    private static async Task<string> RequireValidAsync(string module, ReplicatedSetting declared, JsonElement value, CancellationToken cancellationToken)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new DomainException($"No value was given for setting '{declared.Name}' of module '{module}'.");
        }

        string text = JsonSerializer.Serialize(value);
        JsonSchema schema = await LocalSchemas.ReadAsync(
            declared.Schema.GetRawText(),
            reference => $"The schema of setting '{declared.Name}' of module '{module}' refers to '{reference}', outside itself; a setting's schema refers only to its own definitions.",
            reason => $"The schema of setting '{declared.Name}' of module '{module}' cannot be read: {reason}",
            cancellationToken);

        ICollection<ValidationError> failures = schema.Validate(text);
        return failures.Count == 0
            ? text
            : throw new DomainException(
                $"The value given for setting '{declared.Name}' of module '{module}' does not meet the setting's schema: {string.Join("; ", failures.Select(f => $"{f.Kind} at {f.Path ?? "#"}"))}.");
    }

    private async Task<ModuleSettingsShown> ShownAsync(string tenant, string module, CancellationToken cancellationToken)
    {
        SettingsSchema schema = schemas.Find(module)
            ?? throw new DomainException($"This node holds no settings schema for module '{module}'.");
        ModuleSettings? settings = await FindSettingsAsync(tenant, module, cancellationToken);
        ModuleProfileSelection? selection = await FindSelectionAsync(tenant, module, cancellationToken);

        List<ReplicatedSettingShown> replicated = [.. schema.Replicated.Select(s => Replicated(s, settings?.ValueOf(s.Name)))];
        CredentialProfileShown? profile = schema.CredentialProfile is { } declared
            ? Profile(declared, selection?.Profile, HeldProfiles(module))
            : null;

        List<string> missing =
        [
            .. replicated.Where(s => s.Required && s.Source == SettingSource.Unset).Select(s => s.Name),
            .. profile is { Required: true, Source: SettingSource.Unset or SettingSource.Missing } ? [CredentialProfileName] : Array.Empty<string>(),
        ];
        var configuration = new ModuleConfiguration(missing.Count == 0 ? ModuleState.Configured : ModuleState.Unconfigured, missing);
        return new ModuleSettingsShown(tenant, module, replicated, profile, configuration);
    }

    private static ReplicatedSettingShown Replicated(ReplicatedSetting declared, string? value) =>
        (value, declared.Default) switch
        {
            ({ } set, _) => new(declared.Name, declared.Required, SettingSource.Set, JsonSerializer.Deserialize<JsonElement>(set)),
            (null, { ValueKind: not JsonValueKind.Null } fallback) => new(declared.Name, declared.Required, SettingSource.Default, fallback),
            _ => new(declared.Name, declared.Required, SettingSource.Unset, null),
        };

    /// <summary>
    /// A selected profile the node still holds is the setting's value; one it no longer holds is no
    /// value, and the default never stands in for it, so the module never acts as an identity nobody
    /// selected; with none selected, the node's default identity stands when the setting declares it.
    /// </summary>
    private static CredentialProfileShown Profile(CredentialProfileSetting declared, string? selected, IReadOnlyList<string> held)
    {
        SettingSource source = selected switch
        {
            not null when held.Contains(selected, StringComparer.Ordinal) => SettingSource.Set,
            not null => SettingSource.Missing,
            null when declared.DefaultsToNodeIdentity => SettingSource.Default,
            null => SettingSource.Unset,
        };
        return new CredentialProfileShown(declared.Required, source, selected, held);
    }

    /// <summary>The names of the entries directly inside the module's area, in ordinal order, read now; none when the area is absent.</summary>
    private IReadOnlyList<string> HeldProfiles(string module)
    {
        string area = home.ModuleAreaOf(module);
        return Directory.Exists(area)
            ? [.. Directory.EnumerateFileSystemEntries(area).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)]
            : [];
    }

    private async Task<ModuleSettings?> FindSettingsAsync(string tenant, string module, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<ModuleSettings> found = await session.Query<ModuleSettings>().Where(s => s.Module == module).ToListAsync(cancellationToken);
        return found.Count == 0 ? null : found[0];
    }

    private async Task<ModuleProfileSelection?> FindSelectionAsync(string tenant, string module, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        IReadOnlyList<ModuleProfileSelection> found = await session.Query<ModuleProfileSelection>()
            .Where(s => s.Project == tenant && s.Module == module)
            .ToListAsync(cancellationToken);
        return found.Count == 0 ? null : found[0];
    }
}
