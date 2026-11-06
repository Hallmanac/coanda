using Coanda.Daemon.Store;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A test reads a store's files whole only once the store has closed them, so no lock SQLite holds on them
/// refuses the read.
/// </summary>
/// <remarks>
/// A write holds SQLite's write lock, a byte-range lock on the write-ahead log's shared-memory file. On macOS
/// that lock and the whole-file shared lock .NET takes to read a file refuse each other, so a read made while
/// any write is under way fails with "The process cannot access the file". On Linux the two kinds of lock are
/// independent, so the read cannot fail there either way.
/// </remarks>
public sealed class StoreFilesTests
{
    [Fact]
    public async Task A_stores_files_are_read_whole_though_a_write_held_them_locked_until_the_store_closed()
    {
        using var temp = new TemporaryHome();
        var writer = new SqliteConnection(StoreConfiguration.ConnectionStringOf(temp.Home.DatabasePath));
        writer.Open();
        Execute(writer, "pragma journal_mode = wal");
        Execute(writer, "create table written (value text)");
        SqliteTransaction writing = writer.BeginTransaction(deferred: false);
        Execute(writer, "insert into written values ('held-until-closed')", writing);

        IReadOnlyList<StoreFile> files = await StoreFiles.ReadOnceClosedAsync(
            temp.Home,
            () =>
            {
                writing.Commit();
                writing.Dispose();
                writer.Dispose();
                return ValueTask.CompletedTask;
            });

        // the read sees what the write committed, so it read the files the write held
        Assert.Contains(files, file => file.Content.AsSpan().IndexOf("held-until-closed"u8) >= 0);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}
