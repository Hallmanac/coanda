using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
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
using Coanda.Daemon.Triggers;
using Coanda.Modules.Software;
using Coanda.Tests.Core;
using Coanda.Tests.Sdk;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Node homes for the record export and rebuild tests: each store fixture restored as an earlier build
/// wrote it, the e86428f one extended with what neither fixture holds (a paused flow run recorded with
/// unprefixed performers, a deferred follow with no closes flag, and a session token), and what a test
/// reads of a home to compare it with another.
/// </summary>
public static class RecordHomes
{
    public const string WrittenBy1fed907 = "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-1fed907.sql";
    public const string WrittenByE86428f = "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-e86428f.sql";

    /// <summary>The e86428f home with <see cref="ExtendAsync"/>'s records added.</summary>
    public const string Extended = "extended";

    /// <summary>Every home the rebuild is tested on.</summary>
    public static TheoryData<string> All => new([WrittenBy1fed907, WrittenByE86428f, Extended]);

    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Writes the home named by <paramref name="which"/> into <paramref name="home"/>, which must hold no record yet.</summary>
    public static async Task WriteAsync(NodeHome home, string which)
    {
        Restore(home, which == Extended ? WrittenByE86428f : which);
        if (which == Extended)
        {
            await ExtendAsync(home);
        }
    }

    public static void Restore(NodeHome home, string fixture)
    {
        using SqliteConnection connection = Open(home.DatabasePath);
        using SqliteCommand restore = connection.CreateCommand();
        restore.CommandText = File.ReadAllText(RepositoryFiles.PathOf(fixture));
        restore.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds to a restored e86428f home, as an earlier build would have left them: a flow run of alpha's
    /// build flow on alpha's task, started and dispatched at its first step with that step named
    /// unprefixed, then paused, so no start advances it; a deferred follow of that flow run's end with
    /// no closes flag; a session token issued for it; and <see cref="GatedBuild"/>, published in alpha.
    /// </summary>
    public static async Task ExtendAsync(NodeHome home)
    {
        // Fixed, so two homes extended apart read the same.
        Guid flowRunId = Guid.Parse("01a1ad6c-bc0b-727b-a228-9e5042800001");
        Guid dispatchId = Guid.Parse("01a1ad6c-bc0b-727b-a228-9e5042800002");
        Guid launchId = Guid.Parse("01a1ad6c-bc0b-727b-a228-9e5042800003");
        var now = new DateTimeOffset(2026, 11, 5, 7, 0, 0, TimeSpan.Zero);
        await using (IDocumentStore store = StoreConfiguration.Open(home.DatabasePath))
        {
            Definition build = await new DefinitionResolver(store).ResolveAsync("alpha", DefinitionKind.Flow, "build", None)
                ?? throw new InvalidOperationException("Alpha has no build flow.");
            FlowStep first = Flow.FromJson(build.Content).Steps[0];
            // Named as a build before the carve-out named it, whichever way the flow's content names it.
            string step = (first.SystemStep ?? throw new InvalidOperationException("Alpha's build flow does not begin with a system step."))
                .Replace($"{SoftwareSteps.Module}/", string.Empty, StringComparison.Ordinal);
            Assert.Contains(step, EarlierRecords.UnprefixedSteps);

            Guid parcelId;
            await using (IQuerySession query = store.QuerySession("alpha"))
            {
                parcelId = Assert.Single(await query.Query<Parcel>().ToListAsync(None)).Id;
            }

            await using IDocumentSession session = store.LightweightSession("alpha");
            session.Events.Append(parcelId, new ParcelFlowRunAttached(parcelId, flowRunId, now));
            session.Events.StartStream<FlowRun>(
                flowRunId,
                new FlowRunStarted(flowRunId, parcelId, build.Name, build.VersionNumber, build.ContentHash, Principal.LocalOwner.Id, first.Name, StepKind.SystemStep, step, now),
                new SystemStepDispatched(flowRunId, first.Name, step, dispatchId, now),
                new FlowRunPaused(flowRunId, "Waiting on the owner", "resume", Principal.LocalOwner.Id, now));
            session.Store(new DeferredFollows { Id = parcelId, Pending = [new PendingFollow(flowRunId, "merge")] });
            await session.SaveChangesAsync(None);

            await using IDocumentSession node = store.LightweightSession(Tenants.Node);
            node.Store(new SessionTokenRecord { Id = Convert.ToHexStringLower(SHA256.HashData(launchId.ToByteArray())), Project = "alpha", FlowRunId = flowRunId, LaunchId = launchId });
            await node.SaveChangesAsync(None);
        }

        await PublishGatedBuildAsync(home);
        SqliteConnection.ClearAllPools();
        using SqliteConnection connection = Open(home.DatabasePath);
        using SqliteCommand earlier = connection.CreateCommand();
        earlier.CommandText = "UPDATE fi_doc_deferredfollows SET data = json_remove(data, '$.pending[0].closes')";
        Assert.Equal(1, earlier.ExecuteNonQuery());
    }

    /// <summary>The flow the extended home publishes in alpha: one from before the build flow ended at the open pull request, routing the unprefixed open-pr on done alone.</summary>
    public static Flow GatedBuild => PreviousFlows.BuildWithGatedMerge with { Name = "gated-build" };

    private static async Task PublishGatedBuildAsync(NodeHome home)
    {
        await using IDocumentStore store = StoreConfiguration.Open(home.DatabasePath);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: PreviousFlows.SystemSteps);
        Flow flow = GatedBuild;
        Definition drafted = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, "alpha", flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, "alpha", drafted.Id, None);
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
