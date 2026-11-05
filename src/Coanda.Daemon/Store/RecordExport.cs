using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Daemon.Api;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Transport;
using Coanda.Daemon.Triggers;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;

namespace Coanda.Daemon.Store;

/// <summary>
/// Writes a full export of a node's record (<see cref="RecordExportFile"/>) to an owner-only file under
/// its home, holding the home's lock throughout, so it never runs beside a daemon, even one still
/// starting. Before it reads the record it opens the store as a start would, applying this build's
/// schema, and carries an earlier project record into the software module's settings
/// (<see cref="EarlierProjectRecords"/>), since the export writes the registration without them. Every
/// event is written in its current shape (<see cref="EarlierRecords.Exported"/>), and every document
/// other than an inline snapshot as it is stored, a deferred follow in its current shape and each loop
/// position, and each ledger line's store sequence, renumbered to the export's sequences: a position names
/// the same event it named, or, when that event is left out, the last exported event before it. Nothing is pushed, and nothing is written
/// to any clone or remote.
/// </summary>
public static class RecordExport
{
    /// <summary>The document tables whose documents hold a global event sequence, read as "everything up to here is handled".</summary>
    private static readonly IReadOnlySet<string> PositionTables =
        new HashSet<string>([StoreConfiguration.DocumentTableOf(typeof(TriggerPosition)), StoreConfiguration.DocumentTableOf(typeof(FeedPosition))], StringComparer.Ordinal);

    /// <summary>The ledger's line records, whose <c>storeSequence</c> is where the next ledger export reads from.</summary>
    private static readonly string LedgerLinesTable = StoreConfiguration.DocumentTableOf(typeof(LedgerLine));

    private static readonly string DeferredFollowsTable = StoreConfiguration.DocumentTableOf(typeof(DeferredFollows));

    /// <summary>The columns every document table holds, and the only ones an export carries.</summary>
    private static readonly IReadOnlyList<string> DocumentColumns = ["tenant_id", "id", "data", "dotnet_type", "last_modified"];

    /// <summary>The events table's columns: the ones an export carries, and <c>data_binary</c>, which the store's JSON serializer never fills.</summary>
    private static readonly IReadOnlyList<string> EventColumns =
        ["seq_id", "id", "stream_id", "version", "data", "data_binary", "type", "timestamp", "tenant_id", "dotnet_type", "is_archived"];

    /// <summary>The streams table's columns: the ones an export carries, and <c>compacted_version</c>, which nothing here compacts.</summary>
    private static readonly IReadOnlyList<string> StreamColumns = ["tenant_id", "id", "type", "version", "timestamp", "created", "is_archived", "compacted_version"];

    /// <summary>
    /// Writes the export and returns where it is and what it holds. It refuses, writing no export, when the
    /// home's lock is held or the record cannot be exported; a refusal after the store is opened leaves
    /// whatever the open and the carry wrote, as a start of this build would have.
    /// </summary>
    public static async Task<RecordExportResult> WriteAsync(NodeHome home, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(clock);
        home.EnsureExists();
        using HomeLock held = HomeLock.Take(home, "the export");
        if (!File.Exists(home.DatabasePath))
        {
            throw new DomainException($"The node home {home.Path} holds no record, so there is nothing to export.");
        }

        PreRenameStore.ThrowIfFound(home);
        RecordExportFile export = await ExportAsync(home, clock, cancellationToken);
        string path = await SaveAsync(home, export, clock, cancellationToken);
        return new RecordExportResult(path, export.Events.Count, export.LastEvent);
    }

    private static async Task<RecordExportFile> ExportAsync(NodeHome home, TimeProvider clock, CancellationToken cancellationToken)
    {
        IReadOnlyList<Definition> nodeDefinitions;
        await using (IDocumentStore store = StoreConfiguration.Open(home.DatabasePath))
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(cancellationToken);
            await new EarlierProjectRecords(store, home, clock).CarryAsync(cancellationToken);
            await using IQuerySession session = store.QuerySession(Tenants.Node);
            nodeDefinitions = await session.Query<Definition>().ToListAsync(cancellationToken);
        }

        SqliteConnection.ClearAllPools();
        ForkBase? CurrentBase(DefinitionKind kind, string name, int version)
        {
            ForkBase earlier = ForkBase.Earlier(name, version);
            return nodeDefinitions.FirstOrDefault(d => d.Kind == kind && earlier.IsBaseOf(d)) is { } resolved
                ? ForkBase.Of(resolved)
                : null;
        }

        using SqliteConnection connection = Connect(home.DatabasePath, SqliteOpenMode.ReadOnly);
        using SqliteTransaction reading = connection.BeginTransaction(deferred: true);

        RequireCarried(connection, "fi_events", EventColumns, "SELECT EXISTS (SELECT 1 FROM fi_events WHERE data_binary IS NOT NULL)", "an event held as binary data");
        RequireCarried(connection, "fi_streams", StreamColumns, "SELECT EXISTS (SELECT 1 FROM fi_streams WHERE compacted_version <> 0)", "a compacted stream");

        List<ExportedEvent> events = [];
        List<(long Record, long Export)> kept = [];
        Dictionary<(string Tenant, string Stream), long> omitted = [];
        ExportedEventReference? last = null;
        foreach (StoredEventRow row in ReadEvents(connection))
        {
            last = new ExportedEventReference(row.Id, row.Sequence);
            (string, string) stream = (row.Tenant, row.StreamId);
            if (EarlierRecords.Exported(new StoredEvent(row.Type, row.Data), CurrentBase) is not { } exported)
            {
                omitted[stream] = omitted.GetValueOrDefault(stream) + 1;
                continue;
            }

            long sequence = events.Count + 1;
            events.Add(new ExportedEvent(
                sequence, row.Id, row.Tenant, row.StreamId, row.Version - omitted.GetValueOrDefault(stream), exported.Type, row.DotnetType, row.Timestamp, row.Archived, exported.Data));
            kept.Add((row.Sequence, sequence));
        }

        List<ExportedStream> streams = [];
        foreach (ExportedStream stream in ReadStreams(connection))
        {
            long left = omitted.GetValueOrDefault((stream.Tenant, stream.Id));
            if (left > 0 && left >= stream.Version)
            {
                continue;
            }

            streams.Add(stream with { Version = stream.Version - left });
        }

        long PositionOf(long recorded)
        {
            int index = kept.FindLastIndex(k => k.Record <= recorded);
            return index < 0 ? 0 : kept[index].Export;
        }

        List<ExportedDocumentTable> documents = [];
        foreach (string table in DocumentTables(connection))
        {
            IReadOnlyList<ExportedDocument> rows = ReadDocuments(connection, table);
            documents.Add(new ExportedDocumentTable(
                table,
                Schema(connection, table),
                [.. rows.Select(row => row with { Data = CurrentDocument(table, row.Data, PositionOf) })]));
        }

        return new RecordExportFile(RecordExportFile.CurrentFormat, DaemonHost.Version, clock.GetUtcNow(), last, streams, events, documents);
    }

    /// <summary>
    /// A document's JSON as the export writes it: a loop position, and the store sequence a ledger line
    /// records, in the export's sequences; a deferred follow in its current shape; and any other as it is
    /// stored. A ledger line's own <c>sequence</c> is its origin's chain sequence and is never renumbered.
    /// </summary>
    private static string CurrentDocument(string table, string data, Func<long, long> positionOf)
    {
        if (table == DeferredFollowsTable)
        {
            return EarlierRecords.CurrentDeferredFollows(data);
        }

        string? member = table == LedgerLinesTable
            ? "storeSequence"
            : PositionTables.Contains(table) ? "sequence" : null;
        if (member is null
            || JsonNode.Parse(data) is not JsonObject position
            || position[member] is not JsonValue sequence
            || sequence.GetValueKind() != JsonValueKind.Number)
        {
            return data;
        }

        position[member] = positionOf(sequence.GetValue<long>());
        return position.ToJsonString();
    }

    private static async Task<string> SaveAsync(NodeHome home, RecordExportFile export, TimeProvider clock, CancellationToken cancellationToken)
    {
        OwnerOnly.EnsurePrivateDirectory(home.ExportsPath);
        string path = Path.Combine(home.ExportsPath, $"record-{clock.GetUtcNow():yyyyMMdd'T'HHmmssfff'Z'}.json");

        // Created here or not at all, so a failure part way deletes only what this export wrote.
        FileStream file = OwnerOnly.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            await using (file)
            {
                await JsonSerializer.SerializeAsync(file, export, RecordExportFile.Json, cancellationToken);
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        return path;
    }

    /// <summary>
    /// Opens a record to read without changing any file of it: with no write-ahead log or rollback journal
    /// beside it, as immutable, so SQLite creates no companion file; with either, read-only, so SQLite
    /// reads it as any reader does.
    /// </summary>
    internal static SqliteConnection ConnectUnchanged(string databasePath)
    {
        if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-journal"))
        {
            return Connect(databasePath, SqliteOpenMode.ReadOnly);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = $"{new Uri(Path.GetFullPath(databasePath)).AbsoluteUri}?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    internal static SqliteConnection Connect(string databasePath, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = mode, Pooling = false };
        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>The record's last event by its id and sequence, or null for a record holding none.</summary>
    internal static ExportedEventReference? LastEvent(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, seq_id FROM fi_events ORDER BY seq_id DESC LIMIT 1";
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new ExportedEventReference(reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static List<StoredEventRow> ReadEvents(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT seq_id, id, tenant_id, stream_id, version, type, dotnet_type, timestamp, is_archived, data FROM fi_events ORDER BY seq_id";
        using SqliteDataReader reader = command.ExecuteReader();
        List<StoredEventRow> rows = [];
        while (reader.Read())
        {
            rows.Add(new StoredEventRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetInt64(8) != 0,
                reader.GetString(9)));
        }

        return rows;
    }

    private static List<ExportedStream> ReadStreams(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT tenant_id, id, type, version, timestamp, created, is_archived FROM fi_streams ORDER BY created, tenant_id, id";
        using SqliteDataReader reader = command.ExecuteReader();
        List<ExportedStream> streams = [];
        while (reader.Read())
        {
            streams.Add(new ExportedStream(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6) != 0));
        }

        return streams;
    }

    /// <summary>Every document table the record holds other than an inline snapshot's, by name.</summary>
    internal static IReadOnlyList<string> DocumentTables(SqliteConnection connection)
    {
        HashSet<string> inline = [.. StoreConfiguration.InlineSnapshots.Select(s => StoreConfiguration.DocumentTableOf(s.Type))];
        return [.. AllDocumentTables(connection).Where(t => !inline.Contains(t))];
    }

    /// <summary>Every document table the database holds, by name.</summary>
    internal static IReadOnlyList<string> AllDocumentTables(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'fi\_doc\_%' ESCAPE '\' ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> tables = [];
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    /// <summary>The statements that create <paramref name="table"/> and its indexes, the table's first.</summary>
    private static IReadOnlyList<string> Schema(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE tbl_name = $table AND sql IS NOT NULL ORDER BY type = 'table' DESC, name";
        command.Parameters.AddWithValue("$table", table);
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> statements = [];
        while (reader.Read())
        {
            statements.Add(reader.GetString(0));
        }

        return statements;
    }

    /// <summary>
    /// Refuses a record whose <paramref name="table"/> holds a column other than <paramref name="columns"/>,
    /// or anything <paramref name="uncarried"/> finds, which it names as <paramref name="what"/>: an export
    /// carries neither, and a rebuild would lose it.
    /// </summary>
    private static void RequireCarried(SqliteConnection connection, string table, IReadOnlyList<string> columns, string uncarried, string what)
    {
        using (SqliteCommand named = connection.CreateCommand())
        {
            named.CommandText = "SELECT name FROM pragma_table_xinfo($table) WHERE hidden NOT IN (2, 3)";
            named.Parameters.AddWithValue("$table", table);
            using SqliteDataReader reader = named.ExecuteReader();
            while (reader.Read())
            {
                string column = reader.GetString(0);
                if (!columns.Contains(column, StringComparer.Ordinal))
                {
                    throw new DomainException($"The record's {table} table holds a column, {column}, that an export does not carry, so nothing was exported.");
                }
            }
        }

        using SqliteCommand found = connection.CreateCommand();
        found.CommandText = uncarried;
        if (found.ExecuteScalar() is 1L)
        {
            throw new DomainException($"The record holds {what}, which an export does not carry, so nothing was exported.");
        }
    }

    /// <summary>The documents of <paramref name="table"/>, refused when the table holds a column an export does not carry.</summary>
    private static IReadOnlyList<ExportedDocument> ReadDocuments(SqliteConnection connection, string table)
    {
        using (SqliteCommand columns = connection.CreateCommand())
        {
            // Hidden columns (2 and 3) are generated, and are regenerated from the data.
            columns.CommandText = "SELECT name FROM pragma_table_xinfo($table) WHERE hidden NOT IN (2, 3)";
            columns.Parameters.AddWithValue("$table", table);
            using SqliteDataReader reader = columns.ExecuteReader();
            while (reader.Read())
            {
                string column = reader.GetString(0);
                if (!DocumentColumns.Contains(column, StringComparer.Ordinal))
                {
                    throw new DomainException($"The document table {table} holds a column, {column}, that an export does not carry, so nothing was exported.");
                }
            }
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT tenant_id, id, data, dotnet_type, last_modified FROM \"{table}\" ORDER BY tenant_id, id";
        using SqliteDataReader rows = command.ExecuteReader();
        List<ExportedDocument> documents = [];
        while (rows.Read())
        {
            documents.Add(new ExportedDocument(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.IsDBNull(3) ? null : rows.GetString(3), rows.GetString(4)));
        }

        return documents;
    }

    private sealed record StoredEventRow(
        long Sequence, string Id, string Tenant, string StreamId, long Version, string Type, string? DotnetType, string Timestamp, bool Archived, string Data);
}

/// <summary>Where an export was written, how many events it holds, and the record's last event it names.</summary>
public sealed record RecordExportResult(string Path, int Events, ExportedEventReference? LastEvent);
