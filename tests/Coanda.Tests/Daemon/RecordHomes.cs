using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Coanda.Tests.Sdk;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Node homes for the record export, rebuild, and refusal tests: each store fixture restored as the build
/// that wrote it left it, and what a test reads of a home to compare it with another. The two written by
/// earlier builds hold earlier shapes, which this build refuses; the rebuilt one holds current shapes only.
/// </summary>
public static class RecordHomes
{
    public const string WrittenBy1fed907 = "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-1fed907.sql";
    public const string WrittenByE86428f = "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-e86428f.sql";

    /// <summary>
    /// A home restored from <see cref="WrittenByE86428f"/>, extended as the export-and-rebuild build's own test
    /// extended it (a paused flow run of alpha's build flow recorded with unprefixed performers, a deferred
    /// follow of its end, a session token issued for it, and a flow from before the build flow ended at the open
    /// pull request, published in alpha as <c>gated-build</c>), then exported and rebuilt by the build at
    /// <see cref="EarlierShapes.RebuildCommit"/>.
    /// </summary>
    public const string RebuiltByEe75e50 = "tests/Coanda.Tests/Daemon/Fixtures/store-rebuilt-by-ee75e50.sql";

    /// <summary>The name of the flow the rebuilt home publishes in alpha, which routes the unprefixed open-pr on done alone.</summary>
    public const string GatedBuild = "gated-build";

    private static readonly CancellationToken None = CancellationToken.None;

    public static void Restore(NodeHome home, string fixture)
    {
        using SqliteConnection connection = Open(home.DatabasePath);
        using SqliteCommand restore = connection.CreateCommand();
        restore.CommandText = File.ReadAllText(RepositoryFiles.PathOf(fixture));
        restore.ExecuteNonQuery();
    }

    /// <summary>
    /// What the daemon's services read of <paramref name="daemon"/>'s home: its projects; each project's
    /// parcels, flow runs, and software module settings; and every definition each tenant holds, with its
    /// content, content hash, version number, and the definition its fork base resolves to. A definition's
    /// own id and times are left out, since a start imports a module's new definitions afresh.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReadAsync(RunningDaemon daemon)
    {
        IServiceProvider services = daemon.Services;
        IDocumentStore store = services.GetRequiredService<IDocumentStore>();
        var resolver = new DefinitionResolver(store);
        Principal owner = Principal.LocalOwner;
        List<string> read = [];
        IReadOnlyList<Project> projects = await services.GetRequiredService<ProjectService>().ListAsync(owner, None);
        foreach (Project project in projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            read.Add($"project {Json(project)}");
            IReadOnlyList<Parcel> parcels = await services.GetRequiredService<ParcelService>().ListAsync(owner, project.Name, None);
            read.AddRange(parcels.OrderBy(p => p.Id).Select(p => $"parcel {Json(p)}"));
            IReadOnlyList<FlowRun> flowRuns = await services.GetRequiredService<FlowRunService>().ListAsync(owner, project.Name, null, None);
            read.AddRange(flowRuns.OrderBy(f => f.Id).Select(f => $"flow run {Json(f)}"));
            ModuleSettingsShown settings = await services.GetRequiredService<ModuleSettingsService>().ShowAsync(owner, project.Name, SoftwareSteps.Module, None);
            read.Add($"settings {Json(settings)}");
        }

        foreach (string tenant in projects.Select(p => p.Name).Append(Tenants.Node).Order(StringComparer.Ordinal))
        {
            await using IQuerySession session = store.QuerySession(tenant);
            foreach (Definition definition in (await session.Query<Definition>().ToListAsync(None))
                .OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Scope).ThenBy(d => d.VersionNumber))
            {
                Definition? forkedFrom = await resolver.ForkedFromAsync(definition, None);
                read.Add($"definition {tenant} {definition.Kind} {definition.Name} {definition.Scope} {definition.Project} v{definition.VersionNumber} "
                    + $"{definition.ContentHash} {definition.Status} {definition.Module} {definition.ModuleVersion} base:{forkedFrom?.Id} {Flat(definition.Content)}");
            }
        }

        return read;
    }

    /// <summary>The events table's rows as they are stored, each with its id and recorded time, in order.</summary>
    public static IReadOnlyList<(string Id, string Stream, string Tenant, string Timestamp, string Type, string Data)> Events(string databasePath)
    {
        using SqliteConnection connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, stream_id, tenant_id, timestamp, type, data FROM fi_events ORDER BY seq_id";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string, string, string, string, string)> rows = [];
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>The rows of one document table, by tenant and id, with their JSON.</summary>
    public static IReadOnlyList<(string Tenant, string Id, string Data)> Documents(string databasePath, string table)
    {
        using SqliteConnection connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT tenant_id, id, data FROM \"{table}\" ORDER BY tenant_id, id";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string, string)> rows = [];
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// Every file under <paramref name="path"/>, by its path relative to it, with a hash of its content; the
    /// home's lock file, which holds nothing and which a running daemon holds, is listed without one.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Files(string path) => Files(path, new HashSet<string>());

    /// <summary>
    /// Every file under <paramref name="path"/> but those <paramref name="leftOut"/> names by their relative
    /// paths, which are never read: Windows refuses a read of a file a running daemon holds open.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Files(string path, IReadOnlySet<string> leftOut) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => !leftOut.Contains(Path.GetRelativePath(path, f)))
            .ToDictionary(
                f => Path.GetRelativePath(path, f),
                f => Path.GetFileName(f) == "home.lock"
                    ? "lock"
                    : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))),
                StringComparer.Ordinal);

    /// <summary>Asserts the file is readable and writable by its owner alone, by whichever of the two the platform has.</summary>
    public static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertSingleOwnerRule(new FileInfo(path).GetAccessControl());
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    public static SqliteConnection Open(string databasePath, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = mode, Pooling = false };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    [SupportedOSPlatform("windows")]
    private static void AssertSingleOwnerRule(FileSystemSecurity security)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        FileSystemAccessRule rule = Assert.IsType<FileSystemAccessRule>(Assert.Single(rules));
        Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
        Assert.Equal((AccessControlType.Allow, FileSystemRights.FullControl), (rule.AccessControlType, rule.FileSystemRights));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>A definition's content as the definition it reads as, since the fixtures' text was written on macOS and the line endings differ by platform.</summary>
    private static string Flat(string content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        return JsonSerializer.Serialize(document.RootElement);
    }
}
