using System.Text;
using System.Text.Json.Nodes;
using Coanda.Core.Projects;
using Coanda.Daemon;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Commands;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// Exporting a home the export-and-rebuild build rebuilt writes each project's events as the store holds
/// them, which is their current shape: every line reads back as this build's event type for its type name
/// and carries the stored event's data, the registration carries its repos and nothing of the registration
/// from before repos, a fork base is written as stored, and the node tenant is not written.
/// </summary>
public sealed class LedgerExportFixtureTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Exporting_a_rebuilt_home_writes_each_projects_events_as_stored_and_nothing_of_the_node_tenant()
    {
        using var temp = new TemporaryHome();
        RecordHomes.Restore(temp.Home, RecordHomes.RebuiltByEe75e50);
        IReadOnlyList<StoredRow> rows = EarlierFlowRuns.Rows(temp.Home.DatabasePath);
        IReadOnlyList<(string Tenant, string Type, Guid Id)> stored = EventIds(temp.Home.DatabasePath);
        NodeIdentity identity = await NodeIdentity.EnsureAsync(temp.Home, None);
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        using var export = new LedgerExport(store, temp.Home, identity, new ProcessCommandRunner(), TimeProvider.System, NullLogger<LedgerExport>.Instance);

        IReadOnlyList<LedgerPublication> round = await export.ExportAsync(None);

        Assert.NotEmpty(round);
        Dictionary<string, Type> types = await EventTypesAsync(store, [Tenants.Node, .. round.Select(p => p.Project)]);
        foreach (LedgerPublication published in round)
        {
            Project project = await ProjectAsync(store, published.Project);
            IReadOnlyList<SignedLine> lines = await LinesAsync(store, published.Project, identity);

            // One line for every event of the project's tenant, and none for any other tenant's.
            Assert.Equal(stored.Where(e => e.Tenant == published.Project).Select(e => e.Id), lines.Select(l => l.Content.EventId));
            ChainVerification verified = ChainVerifier.Verify([.. lines.Select(l => l.Bytes)], project.Id, identity.NodeId, [identity.Key.PublicKey]);
            Assert.Equal((ChainOutcome.Verified, lines.Count), (verified.Outcome, verified.VerifiedLines));

            foreach (SignedLine line in lines)
            {
                object current = store.Options.Serializer.FromJson(types[line.Content.Event.Type], line.Content.Event.Data);
                Assert.Equal(line.Content.Event.Data, store.Options.Serializer.ToJson(current));
            }

            JsonObject registered = Data(Assert.Single(lines, l => l.Content.Event.Type == "project_registered"));
            Assert.Equal(["projectId", "name", "repos", "registeredBy", "registeredAt"], registered.Select(member => member.Key));
        }

        // Alpha's fork of the build flow is written with the fork base it was stored with, in its current shape.
        StoredRow drafted = rows.Single(r => r.Tenant == "alpha" && r.Type == "definition_drafted" && JsonNode.Parse(r.Data)?["forkBase"] is JsonObject);
        JsonObject before = JsonNode.Parse(drafted.Data)?.AsObject() ?? throw new InvalidOperationException("The stored draft is not an object.");
        JsonObject written = Data(Assert.Single(await LinesAsync(store, "alpha", identity), l => l.Content.Event.Type == "definition_drafted" && Data(l)["forkBase"] is JsonObject));
        Assert.NotNull(before["forkBase"]?["contentHash"]);
        Assert.True(JsonNode.DeepEquals(before, written));
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
}
