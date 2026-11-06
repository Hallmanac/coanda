using Fisher;
using Fisher.Services;
using Microsoft.Data.Sqlite;

namespace Coanda.Daemon.Store;

/// <summary>
/// Fisher's store over a node home's record, which keeps the record's write-ahead log open from its first
/// commit until its disposal, and whose disposal returns only once every SQLite connection the store opened
/// has closed the record's files.
/// </summary>
/// <remarks>
/// <para>
/// The store's connections are unpooled (<see cref="StoreConfiguration.ConnectionStringOf"/>), so a session's
/// close closes its connection, and SQLite checkpoints and deletes the write-ahead log and its shared-memory
/// file whenever the last connection to the record closes. The store therefore holds one connection of its
/// own from its first commit, once the record is in write-ahead-log mode, which keeps both files for as long
/// as the store is open, as the pool's idle connections used to.
/// </para>
/// <para>
/// Fisher 1.27.0 disposes the reader of a query's command but not the command (<c>FisherQueryProvider.ExecuteReaderAsync</c>),
/// so the command's prepared statement stays on the connection until the connection closes and disposes
/// the commands it still holds. It holds them weakly: when the collector takes the command first, the close
/// passes over it, and its statement is left to the finalizer thread. Closing the connection while that
/// statement may still be unfinalized makes SQLite defer the close, keeping the record's files open until
/// the statement is finalized and only then closing them; when that is the last connection to the record,
/// the late close checkpoints the write-ahead log under an exclusive lock.
/// </para>
/// <para>
/// The collection that took the command queued its statement for finalization, so waiting for the
/// finalizers that are pending once Fisher's disposal returns waits for every such deferred close to end.
/// A statement still reachable when its connection closed is disposed by that close and needs no wait.
/// </para>
/// </remarks>
public sealed class HomeStore : DocumentStore, IAsyncDisposable, IDisposable
{
    private readonly RecordHold hold;

    public HomeStore(StoreOptions options)
        : this(options, new RecordHold(options.ConnectionString))
    {
    }

    private HomeStore(StoreOptions options, RecordHold hold)
        : base(Listening(options, hold)) =>
        this.hold = hold;

    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        hold.Dispose();
        GC.WaitForPendingFinalizers();
    }

    public new void Dispose()
    {
        base.Dispose();
        hold.Dispose();
        GC.WaitForPendingFinalizers();
    }

    private static StoreOptions Listening(StoreOptions options, RecordHold hold)
    {
        options.Listeners.Add(hold);
        return options;
    }

    /// <summary>
    /// The store's own connection to the record, opened after the store's first commit and read from once,
    /// which is what makes SQLite count it among the connections using the write-ahead log.
    /// </summary>
    private sealed class RecordHold(string connectionString) : IDocumentSessionListener, IDisposable
    {
        private readonly Lock gate = new();
        private SqliteConnection? held;
        private bool released;

        public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token) => Task.CompletedTask;

        public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        {
            lock (gate)
            {
                if (held is null && !released)
                {
                    held = TryJoin();
                }
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (gate)
            {
                released = true;
                held?.Dispose();
                held = null;
            }
        }

        /// <summary>
        /// Opens and reads once, or leaves the hold to the next commit when SQLite refuses: the commit this
        /// follows has already succeeded, so a refusal here is no failure of it.
        /// </summary>
        private SqliteConnection? TryJoin()
        {
            var connection = new SqliteConnection(connectionString);
            try
            {
                connection.Open();
                using SqliteCommand read = connection.CreateCommand();
                read.CommandText = "select count(*) from sqlite_master";
                read.ExecuteScalar();
                return connection;
            }
            catch (SqliteException)
            {
                connection.Dispose();
                return null;
            }
        }
    }
}
