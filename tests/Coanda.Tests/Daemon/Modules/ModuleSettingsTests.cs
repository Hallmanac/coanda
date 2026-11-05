using System.Text;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Modules;
using Coanda.Core.Modules.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A module's settings in a project, in two scopes: replicated settings recorded in the project's own
/// tenant and checked against the module's settings schema, and the credential profile selected on this
/// node, recorded by name in the node tenant and read against the module's area at the time of the call.
/// The module is configured unless a setting it requires has no value and no default.
/// </summary>
public sealed class ModuleSettingsTests
{
    private const string On = ModuleNode.On;
    private const string Off = ModuleNode.Off;
    private const string Strict = ModuleNode.Module;
    private const string Relaxed = StandInModules.Relaxed;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_replicated_setting_is_recorded_in_the_projects_tenant_and_a_later_value_replaces_the_earlier()
    {
        await using ModuleNode node = await StartAsync();
        ModuleSettingsService settings = Settings(node);

        await settings.SetAsync(Principal.LocalOwner, On, Strict, "channel", Json("\"#builds\""), None);
        ModuleSettingsShown shown = await settings.SetAsync(Principal.LocalOwner, On, Strict, "channel", Json("\"#releases\""), None);
        await settings.SetAsync(Principal.LocalOwner, On, Strict, "labels", Json("""["a", "b"]"""), None);

        ReplicatedSettingShown channel = shown.Replicated.Single(s => s.Name == "channel");
        Assert.Equal(SettingSource.Set, channel.Source);
        Assert.Equal("\"#releases\"", channel.Value?.GetRawText());
        ModuleSettings recorded = Assert.Single(await DocumentsAsync<ModuleSettings>(node, On));
        Assert.Equal(Strict, recorded.Module);
        Assert.Equal("\"#releases\"", recorded.ValueOf("channel"));
        Assert.Equal("[\"a\",\"b\"]", recorded.ValueOf("labels"));
        Assert.Equal(
            [("channel", "\"#builds\""), ("channel", "\"#releases\""), ("labels", "[\"a\",\"b\"]")],
            (await EventsAsync(node, On, recorded.Id)).OfType<ModuleSettingSet>().Select(e => (e.Name, e.Value)));
        Assert.Empty(await DocumentsAsync<ModuleSettings>(node, Tenants.Node));
        Assert.Empty(await DocumentsAsync<ModuleSettings>(node, Off));
    }

    public static TheoryData<string, string, string, string, string> RefusedSettings => new()
    {
        { "a name the schema does not declare", Strict, "colour", "\"red\"", "Module 'test' declares no replicated setting named 'colour'; it declares 'channel', 'limit', 'labels'." },
        { "a value the schema refuses", Strict, "limit", "0", "The value given for setting 'limit' of module 'test' does not meet the setting's schema: NumberTooSmall at #/." },
        { "a value of another kind", Strict, "channel", "3", "The value given for setting 'channel' of module 'test' does not meet the setting's schema: StringExpected at #/." },
        { "a module the node holds no settings schema for", StandInModules.Unknown, "channel", "\"x\"", "This node holds no settings schema for module 'unknown', so it has no settings this node can set or show; a module declares its settings." },
        { "a module not switched on for the project", "dormant", "channel", "\"x\"", "Module 'dormant' is not switched on for project 'alpha'; a module's settings are set and shown only in a project it is switched on for." },
        { "a module whose name is not a module name", "../test", "channel", "\"x\"", "A module's name is lowercase letters and digits in runs joined by single hyphens; '../test' is not." },
    };

    [Theory]
    [MemberData(nameof(RefusedSettings))]
    public async Task A_replicated_setting_the_rules_refuse_is_refused_with_nothing_recorded(string refused, string module, string name, string value, string rule)
    {
        Assert.False(string.IsNullOrEmpty(refused));
        await using ModuleNode node = await StartAsync();

        DomainException exception = await Assert.ThrowsAsync<DomainException>(
            () => Settings(node).SetAsync(Principal.LocalOwner, On, module, name, Json(value), None));

        Assert.Equal(rule, exception.Message);
        Assert.Empty(await DocumentsAsync<ModuleSettings>(node, On));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_setting_whose_schema_refers_outside_itself_is_refused_without_reading_what_it_names(bool toFile)
    {
        // A file that holds a schema the value would fail, so reading it would show as a different refusal.
        string file = Path.Combine(Path.GetTempPath(), "coanda-tests", $"{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? Path.GetTempPath());
        await File.WriteAllTextAsync(file, """{ "type": "integer" }""", None);
        string reference = toFile ? file.Replace('\\', '/') : "https://example.invalid/schema.json";
        var schemas = new ModuleSettingsSchemas(new Dictionary<string, Coanda.Sdk.Contract.SettingsSchema>
        {
            [Strict] = new(
            [
                new("outside", Json($$"""{ "$ref": "{{reference}}" }"""), Required: false),
                new("inside", Json("""{ "$ref": "#/definitions/branch", "definitions": { "branch": { "type": "string" } } }"""), Required: false),
            ]),
        });
        await using ModuleNode node = await ModuleNode.StartAsync(schemas);
        ModuleSettingsService settings = Settings(node);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => settings.SetAsync(Principal.LocalOwner, On, Strict, "outside", Json("\"x\""), None));

        // A file is refused before any read, for want of a document path, and a URL by the daemon's own resolver.
        Assert.StartsWith("The schema of setting 'outside' of module 'test' ", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not meet", refused.Message, StringComparison.Ordinal);
        if (!toFile)
        {
            Assert.Equal($"The schema of setting 'outside' of module 'test' refers to '{reference}', outside itself; a setting's schema refers only to its own definitions.", refused.Message);
        }

        Assert.Contains(reference.Split('/')[^1], refused.Message, StringComparison.Ordinal);
        File.Delete(file);
        Assert.Empty(await DocumentsAsync<ModuleSettings>(node, On));
        await settings.SetAsync(Principal.LocalOwner, On, Strict, "inside", Json("\"main\""), None);
        DomainException local = await Assert.ThrowsAsync<DomainException>(() => settings.SetAsync(Principal.LocalOwner, On, Strict, "inside", Json("3"), None));
        Assert.StartsWith("The value given for setting 'inside' of module 'test' does not meet the setting's schema:", local.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_module_with_nothing_set_is_configured_when_its_defaults_cover_what_it_requires_the_profile_by_the_nodes_default_identity()
    {
        await using ModuleNode node = await StartAsync();
        ModuleSettingsService settings = Settings(node);

        ModuleSettingsShown shown = await settings.ShowAsync(Principal.LocalOwner, On, Relaxed, None);

        Assert.Equal(new ModuleConfiguration(ModuleState.Configured, []), await settings.StateAsync(On, Relaxed, None), ConfigurationComparer.Instance);
        ReplicatedSettingShown channel = Assert.Single(shown.Replicated);
        Assert.Equal((SettingSource.Default, "\"general\""), (channel.Source, channel.Value?.GetRawText()));
        Assert.Equal(SettingSource.Default, shown.CredentialProfile?.Source);
        Assert.Null(shown.CredentialProfile?.Profile);
    }

    [Fact]
    public async Task A_module_is_unconfigured_until_its_required_setting_is_given_and_a_profile_is_selected()
    {
        await using ModuleNode node = await StartAsync();
        ModuleSettingsService settings = Settings(node);

        Assert.Equal(["channel", "credential-profile"], (await AssertUnconfiguredAsync(settings, Strict)).Missing);

        await settings.SetAsync(Principal.LocalOwner, On, Strict, "channel", Json("\"#builds\""), None);
        Assert.Equal(["credential-profile"], (await AssertUnconfiguredAsync(settings, Strict)).Missing);

        MakeProfile(node, Strict, "work");
        ModuleSettingsShown shown = await settings.SelectProfileAsync(Principal.LocalOwner, On, Strict, "work", None);

        Assert.Equal(ModuleState.Configured, shown.Configuration.State);
        Assert.Equal(new ModuleConfiguration(ModuleState.Configured, []), await settings.StateAsync(On, Strict, None), ConfigurationComparer.Instance);
        Assert.Equal((SettingSource.Set, "work"), (shown.CredentialProfile?.Source, shown.CredentialProfile?.Profile));
        ReplicatedSettingShown limit = shown.Replicated.Single(s => s.Name == "limit");
        Assert.Equal((SettingSource.Default, "3"), (limit.Source, limit.Value?.GetRawText()));
        Assert.Equal(SettingSource.Unset, shown.Replicated.Single(s => s.Name == "labels").Source);
    }

    [Theory]
    [InlineData(Strict)]
    [InlineData(Relaxed)]
    public async Task A_selected_profile_removed_from_the_area_leaves_the_module_on_and_unconfigured_even_when_the_setting_defaults(string module)
    {
        await using ModuleNode node = await StartAsync();
        ModuleSettingsService settings = Settings(node);
        await settings.SetAsync(Principal.LocalOwner, On, module, "channel", Json("\"#builds\""), None);
        string profile = MakeProfile(node, module, "work");
        await settings.SelectProfileAsync(Principal.LocalOwner, On, module, "work", None);
        Assert.Equal(ModuleState.Configured, (await settings.StateAsync(On, module, None)).State);

        Directory.Delete(profile, recursive: true);

        Assert.Equal(["credential-profile"], (await AssertUnconfiguredAsync(settings, module)).Missing);
        ModuleSettingsShown shown = await settings.ShowAsync(Principal.LocalOwner, On, module, None);
        Assert.Equal((SettingSource.Missing, "work"), (shown.CredentialProfile?.Source, shown.CredentialProfile?.Profile));
        Assert.Empty(shown.CredentialProfile?.Held ?? ["not shown"]);

        // Putting the profile back makes it a value again: the area is read at the time of the call.
        MakeProfile(node, module, "work");
        Assert.Equal(ModuleState.Configured, (await settings.StateAsync(On, module, None)).State);
    }

    [Fact]
    public async Task A_selection_is_recorded_in_the_node_tenant_and_appears_in_no_event_or_document_of_the_projects_tenant()
    {
        await using ModuleNode node = await StartAsync();
        ModuleSettingsService settings = Settings(node);
        const string Profile = "selected-profile-4f1c";
        MakeProfile(node, Strict, Profile);
        MakeProfile(node, Strict, "later");

        await settings.SelectProfileAsync(Principal.LocalOwner, On, Strict, Profile, None);
        await settings.SetAsync(Principal.LocalOwner, On, Strict, "channel", Json("\"#builds\""), None);

        ModuleProfileSelection selection = Assert.Single(await DocumentsAsync<ModuleProfileSelection>(node, Tenants.Node));
        Assert.Equal((On, Strict, Profile), (selection.Project, selection.Module, selection.Profile));
        Assert.Empty(await DocumentsAsync<ModuleProfileSelection>(node, On));
        Assert.DoesNotContain(Profile, ProjectRowsText(node, On), StringComparison.Ordinal);
        Assert.Contains(Profile, ProjectRowsText(node, Tenants.Node), StringComparison.Ordinal);

        // A later selection replaces the earlier one, in the same stream.
        await settings.SelectProfileAsync(Principal.LocalOwner, On, Strict, "later", None);
        Assert.Equal("later", Assert.Single(await DocumentsAsync<ModuleProfileSelection>(node, Tenants.Node)).Profile);
        Assert.Equal([Profile, "later"], (await EventsAsync(node, Tenants.Node, selection.Id)).OfType<ModuleProfileSelected>().Select(e => e.Profile));
    }

    [Fact]
    public async Task A_marker_in_a_profiles_file_appears_nowhere_in_the_store_after_the_profile_is_selected_and_the_settings_shown()
    {
        await using ModuleNode node = await StartAsync();
        ModuleSettingsService settings = Settings(node);
        const string Marker = "secret-marker-9d2e61b7c4";
        string profile = MakeProfile(node, Strict, "work");
        await File.WriteAllTextAsync(Path.Combine(profile, "token"), Marker, None);
        string flat = MakeProfile(node, Relaxed, "personal", asFile: true);
        await File.WriteAllTextAsync(flat, Marker, None);

        await settings.SelectProfileAsync(Principal.LocalOwner, On, Strict, "work", None);
        await settings.SelectProfileAsync(Principal.LocalOwner, On, Relaxed, "personal", None);
        await settings.ShowAsync(Principal.LocalOwner, On, Strict, None);
        await settings.ShowAsync(Principal.LocalOwner, On, Relaxed, None);
        await settings.StateAsync(On, Strict, None);

        byte[] marker = Encoding.UTF8.GetBytes(Marker);
        byte[] wide = Encoding.Unicode.GetBytes(Marker);
        string[] files = [node.Home.DatabasePath, .. node.Home.DatabaseCompanionPaths.Where(File.Exists)];
        bool namesFound = false;
        foreach (string file in files)
        {
            byte[] content = ReadShared(file);
            Assert.True(content.AsSpan().IndexOf(marker) < 0 && content.AsSpan().IndexOf(wide) < 0, $"{file} holds the profile's marker.");
            namesFound |= content.AsSpan().IndexOf("personal"u8) >= 0;
        }

        // The selected profile's name is in the store, which shows the search reads what was written.
        Assert.True(namesFound, "No file of the store holds the selected profile's name.");
    }

    public static TheoryData<string, string> RefusedProfiles => new()
    {
        { "../other/work", "A credential profile's name is a plain name (letters, digits, hyphens, and underscores); '../other/work' is not." },
        { "..", "A credential profile's name is a plain name (letters, digits, hyphens, and underscores); '..' is not." },
        { "/etc/passwd", "A credential profile's name is a plain name (letters, digits, hyphens, and underscores); '/etc/passwd' is not." },
        { "work/token", "A credential profile's name is a plain name (letters, digits, hyphens, and underscores); 'work/token' is not." },
        { "", "A credential profile's name is a plain name (letters, digits, hyphens, and underscores); '' is not." },
        { "absent", "This node holds no credential profile named 'absent' for module 'test'; the profiles it holds for the module are the entries directly inside *, and it holds 'work'." },
    };

    [Theory]
    [MemberData(nameof(RefusedProfiles))]
    public async Task A_selection_naming_no_entry_directly_inside_the_modules_area_is_refused_with_nothing_recorded(string profile, string rule)
    {
        await using ModuleNode node = await StartAsync();
        MakeProfile(node, Strict, "work");
        MakeProfile(node, Relaxed, "elsewhere");

        DomainException exception = await Assert.ThrowsAsync<DomainException>(
            () => Settings(node).SelectProfileAsync(Principal.LocalOwner, On, Strict, profile, None));

        Assert.Matches(WildcardPattern.ToRegex(rule), exception.Message);
        Assert.Empty(await DocumentsAsync<ModuleProfileSelection>(node, Tenants.Node));
    }

    [Fact]
    public async Task A_profile_of_another_modules_area_or_for_a_module_declaring_no_profile_setting_is_refused()
    {
        await using ModuleNode node = await StartAsync();
        MakeProfile(node, Relaxed, "elsewhere");
        MakeProfile(node, ModuleNode.Other, "work");
        ModuleSettingsService settings = Settings(node);

        DomainException another = await Assert.ThrowsAsync<DomainException>(() => settings.SelectProfileAsync(Principal.LocalOwner, On, Strict, "elsewhere", None));
        DomainException undeclared = await Assert.ThrowsAsync<DomainException>(() => settings.SelectProfileAsync(Principal.LocalOwner, On, ModuleNode.Other, "work", None));
        DomainException off = await Assert.ThrowsAsync<DomainException>(() => settings.SelectProfileAsync(Principal.LocalOwner, Off, Relaxed, "elsewhere", None));

        Assert.EndsWith("and it holds none.", another.Message, StringComparison.Ordinal);
        Assert.Equal("Module 'other' declares no credential profile setting, so no profile is selected for it.", undeclared.Message);
        Assert.Equal("Module 'relaxed' is not switched on for project 'beta'; a module's settings are set and shown only in a project it is switched on for.", off.Message);
        Assert.Empty(await DocumentsAsync<ModuleProfileSelection>(node, Tenants.Node));
    }

    [Fact]
    public async Task A_module_principal_sets_and_shows_its_own_settings_where_it_is_switched_on_and_is_refused_the_rest()
    {
        await using ModuleNode node = await StartAsync();
        await node.SwitchOnAsync(Off, Relaxed);
        ModuleSettingsService settings = Settings(node);
        Principal module = Principal.ForModule(Strict);
        MakeProfile(node, Strict, "work");

        ModuleSettingsShown set = await settings.SetAsync(module, On, Strict, "channel", Json("\"#mine\""), None);
        ModuleSettingsShown shown = await settings.ShowAsync(module, On, Strict, None);

        Assert.Equal("\"#mine\"", set.Replicated.Single(s => s.Name == "channel").Value?.GetRawText());
        Assert.Equal(set.Replicated.Select(s => (s.Name, s.Source)), shown.Replicated.Select(s => (s.Name, s.Source)));
        Assert.Equal(
            "'project-module.select-profile' is not open to the Module role.",
            (await Assert.ThrowsAsync<DomainException>(() => settings.SelectProfileAsync(module, On, Strict, "work", None))).Message);
        string notItsOwn = "Module 'test' sets and shows only its own settings, not those of module 'other'.";
        Assert.Equal(notItsOwn, (await Assert.ThrowsAsync<DomainException>(() => settings.SetAsync(module, On, ModuleNode.Other, "mapping", Json("{}"), None))).Message);
        Assert.Equal(notItsOwn, (await Assert.ThrowsAsync<DomainException>(() => settings.ShowAsync(module, On, ModuleNode.Other, None))).Message);
        Assert.Equal(
            ModuleNode.NotSwitchedOn(Strict, Off),
            (await Assert.ThrowsAsync<DomainException>(() => settings.SetAsync(module, Off, Strict, "channel", Json("\"#there\""), None))).Message);
        Assert.Equal(ModuleNode.NotSwitchedOn(Strict, Off), (await Assert.ThrowsAsync<DomainException>(() => settings.ShowAsync(module, Off, Strict, None))).Message);

        Assert.Empty(await DocumentsAsync<ModuleProfileSelection>(node, Tenants.Node));
        Assert.Empty(await DocumentsAsync<ModuleSettings>(node, Off));
        Assert.Equal([Strict], (await DocumentsAsync<ModuleSettings>(node, On)).Select(s => s.Module));
    }

    [Fact]
    public async Task Member_and_agent_principals_show_settings_and_are_refused_setting_either_scope()
    {
        await using ModuleNode node = await StartAsync();
        ModuleSettingsService settings = Settings(node);
        MakeProfile(node, Strict, "work");
        Principal[] readers = [new("ana", Role.Member), Principal.ForSession(new SessionBinding(On, Guid.NewGuid(), Guid.NewGuid())), new("coanda-runner", Role.Agent)];

        foreach (Principal reader in readers)
        {
            Assert.Equal(ModuleState.Unconfigured, (await settings.ShowAsync(reader, On, Strict, None)).Configuration.State);
            Assert.Equal(
                $"'project-module.set' is not open to the {reader.Role} role.",
                (await Assert.ThrowsAsync<DomainException>(() => settings.SetAsync(reader, On, Strict, "channel", Json("\"#x\""), None))).Message);
            Assert.Equal(
                $"'project-module.select-profile' is not open to the {reader.Role} role.",
                (await Assert.ThrowsAsync<DomainException>(() => settings.SelectProfileAsync(reader, On, Strict, "work", None))).Message);
        }

        Assert.Empty(await DocumentsAsync<ModuleSettings>(node, On));
        Assert.Empty(await DocumentsAsync<ModuleProfileSelection>(node, Tenants.Node));
    }

    [Fact]
    public async Task The_state_call_refuses_a_module_not_switched_on_or_with_no_settings_schema_on_the_node()
    {
        await using ModuleNode node = await StartAsync();
        ModuleSettingsService settings = Settings(node);

        Assert.StartsWith("Module 'relaxed' is not switched on for project 'beta';", (await Assert.ThrowsAsync<DomainException>(() => settings.StateAsync(Off, Relaxed, None))).Message, StringComparison.Ordinal);
        Assert.StartsWith("This node holds no settings schema for module 'unknown'", (await Assert.ThrowsAsync<DomainException>(() => settings.StateAsync(On, StandInModules.Unknown, None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_daemon_creates_each_known_modules_area_owner_only_at_start()
    {
        await using ModuleNode node = await StartAsync();

        foreach (string module in new[] { Strict, Relaxed, ModuleNode.Other })
        {
            string area = node.Home.ModuleAreaOf(module);
            Assert.True(Directory.Exists(area), $"{area} was not created.");
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(area));
            }
        }

        Assert.False(Directory.Exists(node.Home.ModuleAreaOf(StandInModules.Unknown)));
    }

    internal static async Task<ModuleNode> StartAsync()
    {
        ModuleNode node = await ModuleNode.StartAsync(StandInModules.Schemas);
        await node.SwitchOnAsync(On, Strict, Relaxed, ModuleNode.Other, StandInModules.Unknown);
        return node;
    }

    private static ModuleSettingsService Settings(ModuleNode node) => node.Services.GetRequiredService<ModuleSettingsService>();

    private static JsonElement Json(string text) => StandInModules.Json(text);

    private static async Task<ModuleConfiguration> AssertUnconfiguredAsync(ModuleSettingsService settings, string module)
    {
        ModuleConfiguration state = await settings.StateAsync(On, module, None);
        Assert.Equal(ModuleState.Unconfigured, state.State);
        return state;
    }

    /// <summary>Makes a profile the way a test may: by writing an entry directly into the module's area.</summary>
    internal static string MakeProfile(ModuleNode node, string module, string name, bool asFile = false)
    {
        string area = node.Home.EnsureModuleArea(module);
        string path = Path.Combine(area, name);
        if (asFile)
        {
            File.WriteAllText(path, string.Empty);
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    private static async Task<IReadOnlyList<T>> DocumentsAsync<T>(ModuleNode node, string tenant) where T : notnull
    {
        await using IQuerySession session = node.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant);
        return await session.Query<T>().ToListAsync(None);
    }

    private static async Task<IReadOnlyList<object>> EventsAsync(ModuleNode node, string tenant, Guid streamId)
    {
        await using IQuerySession session = node.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(streamId, token: None);
        return [.. events.Select(e => e.Data)];
    }

    /// <summary>
    /// Every value of every row the store holds for <paramref name="tenant"/>, in every table that carries
    /// a tenant, events and documents alike, read straight from the database file.
    /// </summary>
    private static string ProjectRowsText(ModuleNode node, string tenant)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = node.Home.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        connection.Open();

        List<string> tables = [];
        using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using SqliteDataReader reader = list.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var text = new StringBuilder();
        int tenanted = 0;
        foreach (string table in tables)
        {
            using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText = $"SELECT name FROM pragma_table_info('{table}') WHERE name = 'tenant_id'";
            if (columns.ExecuteScalar() is null)
            {
                continue;
            }

            tenanted++;
            using SqliteCommand rows = connection.CreateCommand();
            rows.CommandText = $"SELECT * FROM \"{table}\" WHERE tenant_id = $tenant";
            rows.Parameters.AddWithValue("$tenant", tenant);
            using SqliteDataReader reader = rows.ExecuteReader();
            while (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    text.Append(reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                }
            }
        }

        Assert.True(tenanted > 0, $"No table carries a tenant_id column; the tables are {string.Join(", ", tables)}.");
        return text.ToString();
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private sealed class ConfigurationComparer : IEqualityComparer<ModuleConfiguration>
    {
        public static ConfigurationComparer Instance { get; } = new();

        public bool Equals(ModuleConfiguration? x, ModuleConfiguration? y) =>
            x is not null && y is not null && x.State == y.State && x.Missing.SequenceEqual(y.Missing, StringComparer.Ordinal);

        public int GetHashCode(ModuleConfiguration obj) => obj.State.GetHashCode();
    }
}
