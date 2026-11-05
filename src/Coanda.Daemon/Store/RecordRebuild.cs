using Coanda.Core;
using Fisher;
using Microsoft.Data.Sqlite;

namespace Coanda.Daemon.Store;

/// <summary>
/// Rebuilds a node's record from an export of it (<see cref="RecordExportFile"/>), holding the home's
/// lock throughout, so it never runs beside a daemon, even one still starting. It refuses, changing
/// nothing, when the export cannot be read or the record's last event is not the one the export names.
/// The new record is built beside the old one in a database created owner-only: this build's schema,
/// the export's streams, events, and documents as the export holds them, and every inline snapshot
/// regenerated from its stream. Only then is the old database moved aside, with its companion files,
/// into a directory of its own in the home, unchanged and still owner-only, and the new one put in its
/// place. Every other file in the home is left where it was. A failure at any point leaves the home as
/// it was found: what was built is deleted, and anything moved is moved back.
/// </summary>
public static class RecordRebuild
{
    public static async Task<RecordRebuildResult> RunAsync(NodeHome home, string exportPath, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportPath);
        ArgumentNullException.ThrowIfNull(clock);
        home.EnsureExists();
        using HomeLock held = HomeLock.Take(home, "the rebuild");
        RecordExportFile export = await RecordExportFile.ReadAsync(exportPath, cancellationToken);
        if (!File.Exists(home.DatabasePath))
        {
            throw new DomainException($"The node home {home.Path} holds no record, so there is nothing to rebuild and nothing was changed.");
        }

        ExportedEventReference? last;
        using (SqliteConnection connection = RecordExport.ConnectUnchanged(home.DatabasePath))
        {
            last = HasEvents(connection) ? RecordExport.LastEvent(connection) : null;
        }

        if (last != export.LastEvent)
        {
            throw new DomainException(
                $"The record's last event is {Describe(last)}, but the export at {exportPath} names {Describe(export.LastEvent)}, "
                + "so the record has changed since it was exported, the record was already rebuilt from it, or the export is another record's, and nothing was changed. "
                + "To rebuild the record as it stands, export it again and rebuild from that.");
        }

        string stamp = $"{clock.GetUtcNow():yyyyMMdd'T'HHmmssfff'Z'}";
        string building = Path.Combine(home.Path, $"{Path.GetFileName(home.DatabasePath)}.rebuild-{stamp}");

        // Created here, owner-only, before SQLite opens it, so it never has wider access and a failure
        // deletes only what this rebuild made; SQLite gives the files it creates beside it the same mode.
        using (OwnerOnly.Open(building, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
        {
        }

        try
        {
            await BuildAsync(building, export, cancellationToken);
            Compare(home.DatabasePath, building);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabase(building);
            throw;
        }

        string aside = Path.Combine(home.Path, $"record-before-rebuild-{stamp}");
        Swap(home, building, aside);
        return new RecordRebuildResult(aside, export.Events.Count);
    }

    private static async Task BuildAsync(string databasePath, RecordExportFile export, CancellationToken cancellationToken)
    {
        await using (IDocumentStore store = StoreConfiguration.Open(databasePath))
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(cancellationToken);
            Insert(databasePath, export);

            // A stream's snapshot is regenerated as its own type's; a stream recorded with no type, or a
            // type no inline snapshot has, is offered to each, and folds to nothing in any it is not.
            foreach (ExportedStream stream in export.Streams)
            {
                if (!Guid.TryParse(stream.Id, out Guid id))
                {
                    continue;
                }

                IReadOnlyList<InlineSnapshot> snapshots = [.. StoreConfiguration.InlineSnapshots.Where(s => s.StreamType == stream.Type)];
                foreach (InlineSnapshot snapshot in snapshots.Count > 0 ? snapshots : StoreConfiguration.InlineSnapshots)
                {
                    await snapshot.RebuildStreamAsync(store, id, stream.Tenant, cancellationToken);
                }
            }
        }

        SqliteConnection.ClearAllPools();

        // Leaves the new record in one file, with no write-ahead log beside it, for the move into place.
        using SqliteConnection connection = RecordExport.Connect(databasePath, SqliteOpenMode.ReadWrite);
        using SqliteCommand checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode = DELETE;";
        checkpoint.ExecuteNonQuery();
    }

    /// <summary>Writes the export's streams, events, and documents into the new record in one transaction, as the export holds them.</summary>
    private static void Insert(string databasePath, RecordExportFile export)
    {
        using SqliteConnection connection = RecordExport.Connect(databasePath, SqliteOpenMode.ReadWrite);
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand streams = connection.CreateCommand())
        {
            streams.CommandText =
                """
                INSERT INTO fi_streams (tenant_id, id, type, version, timestamp, created, is_archived, compacted_version)
                VALUES ($tenant, $id, $type, $version, $timestamp, $created, $archived, 0)
                """;
            foreach (ExportedStream stream in export.Streams)
            {
                streams.Parameters.Clear();
                streams.Parameters.AddWithValue("$tenant", stream.Tenant);
                streams.Parameters.AddWithValue("$id", stream.Id);
                streams.Parameters.AddWithValue("$type", (object?)stream.Type ?? DBNull.Value);
                streams.Parameters.AddWithValue("$version", stream.Version);
                streams.Parameters.AddWithValue("$timestamp", stream.Timestamp);
                streams.Parameters.AddWithValue("$created", stream.Created);
                streams.Parameters.AddWithValue("$archived", stream.Archived ? 1 : 0);
                streams.ExecuteNonQuery();
            }
        }

        using (SqliteCommand events = connection.CreateCommand())
        {
            events.CommandText =
                """
                INSERT INTO fi_events (seq_id, id, stream_id, version, data, type, timestamp, tenant_id, dotnet_type, is_archived)
                VALUES ($sequence, $id, $stream, $version, $data, $type, $timestamp, $tenant, $dotnetType, $archived)
                """;
            foreach (ExportedEvent exported in export.Events)
            {
                events.Parameters.Clear();
                events.Parameters.AddWithValue("$sequence", exported.Sequence);
                events.Parameters.AddWithValue("$id", exported.Id);
                events.Parameters.AddWithValue("$stream", exported.StreamId);
                events.Parameters.AddWithValue("$version", exported.Version);
                events.Parameters.AddWithValue("$data", exported.Data);
                events.Parameters.AddWithValue("$type", exported.Type);
                events.Parameters.AddWithValue("$timestamp", exported.Timestamp);
                events.Parameters.AddWithValue("$tenant", exported.Tenant);
                events.Parameters.AddWithValue("$dotnetType", (object?)exported.DotnetType ?? DBNull.Value);
                events.Parameters.AddWithValue("$archived", exported.Archived ? 1 : 0);
                events.ExecuteNonQuery();
            }
        }

        HashSet<string> tables = [.. RecordExport.AllDocumentTables(connection)];
        foreach (ExportedDocumentTable table in export.Documents)
        {
            if (!table.Table.StartsWith("fi_doc_", StringComparison.Ordinal) || table.Table.Contains('"'))
            {
                throw new DomainException($"The export names a document table, {table.Table}, that is not one, so nothing was changed.");
            }

            if (tables.Add(table.Table))
            {
                foreach (string statement in table.Schema)
                {
                    using SqliteCommand create = connection.CreateCommand();
                    create.CommandText = statement;
                    create.ExecuteNonQuery();
                }
            }

            using SqliteCommand documents = connection.CreateCommand();
            documents.CommandText =
                $"""
                INSERT INTO "{table.Table}" (tenant_id, id, data, dotnet_type, last_modified)
                VALUES ($tenant, $id, $data, $dotnetType, $lastModified)
                """;
            foreach (ExportedDocument document in table.Rows)
            {
                documents.Parameters.Clear();
                documents.Parameters.AddWithValue("$tenant", document.Tenant);
                documents.Parameters.AddWithValue("$id", document.Id);
                documents.Parameters.AddWithValue("$data", document.Data);
                documents.Parameters.AddWithValue("$dotnetType", (object?)document.DotnetType ?? DBNull.Value);
                documents.Parameters.AddWithValue("$lastModified", document.LastModified);
                documents.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Refuses the new record unless it holds, for every inline snapshot, a document for each one the old
    /// record held and no other, so a stream whose snapshot was not regenerated is caught before the swap.
    /// </summary>
    private static void Compare(string recordPath, string rebuiltPath)
    {
        using SqliteConnection record = RecordExport.ConnectUnchanged(recordPath);
        using SqliteConnection rebuilt = RecordExport.Connect(rebuiltPath, SqliteOpenMode.ReadOnly);
        HashSet<string> recordTables = [.. RecordExport.AllDocumentTables(record)];
        foreach (string table in StoreConfiguration.InlineSnapshots.Select(s => StoreConfiguration.DocumentTableOf(s.Type)))
        {
            IReadOnlySet<(string, string)> before = recordTables.Contains(table) ? Keys(record, table) : new HashSet<(string, string)>();
            IReadOnlySet<(string, string)> after = Keys(rebuilt, table);
            if (!before.SetEquals(after))
            {
                throw new DomainException(
                    $"The rebuilt record holds {after.Count} documents in {table} where the record holds {before.Count}, or not the same ones, so nothing was changed.");
            }
        }
    }

    private static IReadOnlySet<(string Tenant, string Id)> Keys(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT tenant_id, id FROM \"{table}\"";
        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<(string, string)> keys = [];
        while (reader.Read())
        {
            keys.Add((reader.GetString(0), reader.GetString(1)));
        }

        return keys;
    }

    /// <summary>
    /// Moves the database and its companion files into <paramref name="aside"/>, then the new database
    /// into its place; on a failure, moves everything back and deletes the new database.
    /// </summary>
    private static void Swap(NodeHome home, string building, string aside)
    {
        List<(string From, string To)> moved = [];
        bool createdAside = false;
        try
        {
            if (Directory.Exists(aside))
            {
                throw new DomainException($"{aside} already exists, so nothing was changed.");
            }

            OwnerOnly.EnsurePrivateDirectory(aside);
            createdAside = true;
            foreach (string path in (IEnumerable<string>)[home.DatabasePath, .. home.DatabaseCompanionPaths])
            {
                if (File.Exists(path))
                {
                    string to = Path.Combine(aside, Path.GetFileName(path));
                    File.Move(path, to);
                    moved.Add((path, to));
                }
            }

            File.Move(building, home.DatabasePath);
        }
        catch
        {
            foreach ((string from, string to) in Enumerable.Reverse(moved))
            {
                File.Move(to, from);
            }

            if (createdAside && Directory.Exists(aside) && !Directory.EnumerateFileSystemEntries(aside).Any())
            {
                Directory.Delete(aside);
            }

            DeleteDatabase(building);
            throw;
        }
    }

    private static void DeleteDatabase(string path)
    {
        foreach (string file in (IEnumerable<string>)[path, path + "-wal", path + "-shm", path + "-journal"])
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static bool HasEvents(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'fi_events')";
        return command.ExecuteScalar() is 1L;
    }

    private static string Describe(ExportedEventReference? reference) =>
        reference is null
            ? "none"
            : $"{reference.Id} at sequence {reference.Sequence}";
}

/// <summary>Where the record the rebuild replaced was moved, and how many events the rebuilt record holds.</summary>
public sealed record RecordRebuildResult(string MovedAside, int Events);
