using System.Text;
using System.Text.Json.Nodes;
using Coanda.Core.Definitions;
using Coanda.Core.Projects;
using Coanda.Daemon;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Commands;
using Coanda.Tests.Sdk;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// Exporting a home an earlier build wrote, restored from each store fixture, writes only current shapes:
/// every line reads back as this build's event type for its type name, the earlier registration is written
/// with its repo, the earlier checks event this build has no type for is not written, the node tenant is
/// not written, and an earlier fork base is written resolved on this node.
/// </summary>
public sealed class LedgerExportFixtureTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    public static TheoryData<string> Fixtures => new()
    {
        "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-1fed907.sql",
        "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-e86428f.sql",
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Exporting_a_restored_home_writes_each_projects_events_in_current_shapes_and_nothing_of_the_node_tenant(string fixture)
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home, fixture);
        IReadOnlyList<StoredRow> rows = EarlierFlowRuns.Rows(temp.Home.DatabasePath);
        IReadOnlyList<(string Tenant, string Type, Guid Id)> stored = EventIds(temp.Home.DatabasePath);
        NodeIdentity identity = await NodeIdentity.EnsureAsync(temp.Home, None);
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        using var export = new LedgerExport(store, temp.Home, identity, new ProcessCommandRunner(), TimeProvider.System, NullLogger<LedgerExport>.Instance);

        IReadOnlyList<LedgerPublication> round = await export.ExportAsync(None);

        // The fixtures' homes hold no clones, so only the lines are recorded; no line of any project stopped.
        Assert.All(round, p => Assert.DoesNotContain(p.Failures, f => f.Contains("stopped exporting", StringComparison.Ordinal)));
        Assert.Contains(stored, e => e.Type == "project_required_checks_set");
        Dictionary<string, Type> types = await EventTypesAsync(store, [Tenants.Node, .. round.Select(p => p.Project)]);
        foreach (LedgerPublication published in round)
        {
            Project project = await ProjectAsync(store, published.Project);
            IReadOnlyList<SignedLine> lines = await LinesAsync(store, published.Project, identity);

            // One line for every event of the project's tenant this build has a type for, and none for any other tenant's.
            Assert.Equal(
                stored.Where(e => e.Tenant == published.Project && e.Type != "project_required_checks_set").Select(e => e.Id),
                lines.Select(l => l.Content.EventId));
            ChainVerification verified = ChainVerifier.Verify([.. lines.Select(l => l.Bytes)], project.Id, identity.NodeId, [identity.Key.PublicKey]);
            Assert.Equal((ChainOutcome.Verified, lines.Count), (verified.Outcome, verified.VerifiedLines));

            foreach (SignedLine line in lines)
            {
                object current = store.Options.Serializer.FromJson(types[line.Content.Event.Type], line.Content.Event.Data);
                Assert.Equal(line.Content.Event.Data, store.Options.Serializer.ToJson(current));
            }

            // The registration's remote is its one repo, named as the remote names it.
            JsonObject registered = Data(Assert.Single(lines, l => l.Content.Event.Type == "project_registered"));
            string remote = JsonNode.Parse(rows.Single(r => r.Tenant == published.Project && r.Type == "project_registered").Data)?["remote"]?.GetValue<string>()
                ?? throw new InvalidOperationException("The fixture's registration names no remote.");
            Assert.Equal(
                $$"""[{"name":"{{ProjectRepo.NameOf(remote)}}","remote":"{{remote}}"}]""",
                registered["repos"]?.ToJsonString());
            Assert.Equal(["projectId", "name", "repos", "registeredBy", "registeredAt"], registered.Select(member => member.Key));
        }

        if (fixture.Contains("e86428f", StringComparison.Ordinal))
        {
            await TheForkedBuildIsWrittenWithItsBaseResolvedAsync(store, rows, identity);
        }
    }

    /// <summary>
    /// Alpha's build flow was forked from build version 2 in the earlier shape; its line names that base by
    /// the name, module, module version, and content hash this node holds it under, and keeps the draft's
    /// content and content hash as they were stored.
    /// </summary>
    private static async Task TheForkedBuildIsWrittenWithItsBaseResolvedAsync(IDocumentStore store, IReadOnlyList<StoredRow> rows, NodeIdentity identity)
    {
        Definition buildTwo;
        await using (IQuerySession node = store.QuerySession(Tenants.Node))
        {
            buildTwo = (await node.Query<Definition>().ToListAsync(None)).Single(d => d.Kind == DefinitionKind.Flow && d.Name == "build" && d.VersionNumber == 2);
        }

        StoredRow stored = rows.Single(r => r.Tenant == "alpha" && r.Type == "definition_drafted");
        JsonObject before = JsonNode.Parse(stored.Data)?.AsObject() ?? throw new InvalidOperationException("The stored draft is not an object.");
        JsonObject line = Data(Assert.Single(await LinesAsync(store, "alpha", identity), l => l.Content.Event.Type == "definition_drafted"));

        Assert.Equal("""{"name":"build","version":2}""", before["forkBase"]?.ToJsonString());
        Assert.Equal(
            $$"""{"name":"build","module":"software","moduleVersion":"0.1.0","contentHash":"{{buildTwo.ContentHash}}"}""",
            line["forkBase"]?.ToJsonString());
        Assert.Equal(before["content"]?.GetValue<string>(), line["content"]?.GetValue<string>());
        Assert.Equal(before["contentHash"]?.GetValue<string>(), line["contentHash"]?.GetValue<string>());
    }

    private static JsonObject Data(SignedLine line) =>
        JsonNode.Parse(line.Content.Event.Data)?.AsObject() ?? throw new InvalidOperationException("A line's data is not an object.");

    private static async Task<IReadOnlyList<SignedLine>> LinesAsync(IDocumentStore store, string project, NodeIdentity identity)
    {
        await using IQuerySession session = store.QuerySession(project);
        IReadOnlyList<LedgerLine> records = await session.Query<LedgerLine>().ToListAsync(None);
        return [.. records
            .Where(r => r.Origin == identity.NodeId)
            .OrderBy(r => r.Sequence)
            .Select(r => SignedLine.Parse(Encoding.UTF8.GetBytes(r.Line), out string? fault) ?? throw new InvalidOperationException($"A recorded line does not parse: {fault}"))];
    }

    private static async Task<Project> ProjectAsync(IDocumentStore store, string project) =>
        await ProjectService.FindAsync(store, project, None) ?? throw new InvalidOperationException($"No project '{project}'.");

    /// <summary>This build's event type for each type name the tenants hold events of, as the store reads them.</summary>
    private static async Task<Dictionary<string, Type>> EventTypesAsync(IDocumentStore store, IEnumerable<string> tenants)
    {
        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (string tenant in tenants)
        {
            await using IQuerySession session = store.QuerySession(tenant);
            foreach (IEvent e in await session.Events.QueryEventsAsync(e => e.Sequence > 0, None))
            {
                types[e.EventTypeName] = e.Data.GetType();
            }
        }

        return types;
    }

    /// <summary>Each stored event's tenant, type name, and id, in store order, as the events table holds them.</summary>
    private static List<(string Tenant, string Type, Guid Id)> EventIds(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT tenant_id, type, id FROM fi_events ORDER BY seq_id";
        List<(string, string, Guid)> events = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add((reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2))));
        }

        return events;
    }

    private static void Restore(NodeHome home, string fixture)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = home.EnsureExists().DatabasePath, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand restore = connection.CreateCommand();
        restore.CommandText = File.ReadAllText(RepositoryFiles.PathOf(fixture));
        restore.ExecuteNonQuery();
    }
}
