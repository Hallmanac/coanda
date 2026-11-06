using Fisher;

namespace Coanda.Daemon.Store;

/// <summary>
/// Fisher's store over a node home's record, whose disposal returns only once every SQLite connection the
/// store opened has closed the record's files.
/// </summary>
/// <remarks>
/// <para>
/// Fisher 1.27.0 disposes the reader of a query's command but not the command (<c>FisherQueryProvider.ExecuteReaderAsync</c>),
/// so the command's prepared statement stays on the connection until the connection closes and disposes
/// the commands it still holds. It holds them weakly: when the collector takes the command first, the close
/// passes over it, and its statement is left to the finalizer thread. Fisher's own disposal then closes
/// the connection while that statement may still be unfinalized, and SQLite defers such a close, keeping
/// the record's files open until the statement is finalized and only then closing them; when that is the
/// last connection to the record, the late close checkpoints the write-ahead log under an exclusive lock.
/// </para>
/// <para>
/// The collection that took the command queued its statement for finalization, so waiting for the
/// finalizers that are pending once Fisher's disposal returns waits for every such deferred close to end.
/// A statement still reachable when its connection closed is disposed by that close and needs no wait.
/// </para>
/// </remarks>
public sealed class HomeStore(StoreOptions options) : DocumentStore(options), IAsyncDisposable, IDisposable
{
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.WaitForPendingFinalizers();
    }

    public new void Dispose()
    {
        base.Dispose();
        GC.WaitForPendingFinalizers();
    }
}
