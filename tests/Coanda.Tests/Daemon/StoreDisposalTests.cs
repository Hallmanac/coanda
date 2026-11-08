using Coanda.Core.Parcels;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Once a store over a home's record is disposed, nothing it opened holds the record's file, even when the
/// collector took the command a query ran before that query's session closed. Fisher disposes such a
/// command's reader but not the command, so the session's close leaves the command's statement on the
/// connection for the finalizer thread, and the store's disposal then closes that connection while the
/// statement is still unfinalized, which leaves the file open until the finalizer thread gets to it.
/// </summary>
/// <remarks>
/// The finalizer thread is held while the query runs, so the statement is certain to be unfinalized when
/// the store is disposed, and released after <see cref="ReleaseDelay"/>, so a disposal that waits for the
/// finalizers returns. The record is read sharing it with no other open: the connection left open refuses
/// that read on Windows and macOS for as long as it holds the file, while the read a test makes to show a
/// home unchanged is refused on Windows for that long and on macOS only in the moment the late close
/// checkpoints the write-ahead log. On Linux SQLite's locks and the read's are independent, so neither is
/// refused there.
/// </remarks>
[Collection(FinalizerHoldCollection.Name)]
public sealed class StoreDisposalTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly TimeSpan ReleaseDelay = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_disposed_daemon_leaves_its_record_to_a_whole_file_read_though_a_querys_command_was_collected_before_its_session_closed()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.RebuiltByEe75e50);
        using FinalizerHold hold = FinalizerHold.Begin();
        await using (RunningDaemon daemon = await Daemons.StartAsync(home, None))
        {
            await QueryAndCollectAsync(daemon.Services.GetRequiredService<IDocumentStore>());
            await daemon.StopAsync(None);
            hold.ReleaseAfter(ReleaseDelay);
        }

        AssertLetGo(home);
    }

    [Fact]
    public async Task A_disposed_store_leaves_its_record_to_a_whole_file_read_though_a_querys_command_was_collected_before_its_session_closed()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.RebuiltByEe75e50);
        using FinalizerHold hold = FinalizerHold.Begin();
        await using (IDocumentStore store = StoreConfiguration.Open(home.DatabasePath))
        {
            await QueryAndCollectAsync(store);
            hold.ReleaseAfter(ReleaseDelay);
        }

        AssertLetGo(home);
    }

    /// <summary>Runs a query in a session and collects, before the session closes, the command the query ran.</summary>
    private static async Task QueryAndCollectAsync(IDocumentStore store)
    {
        await using IQuerySession session = store.QuerySession("alpha");
        Assert.NotEmpty(await session.Query<Parcel>().ToListAsync(None));
        GC.Collect();
    }

    /// <summary>
    /// Asserts the record reads whole sharing it with no other open, and that no file of the home changes
    /// once the finalizers that were held have run.
    /// </summary>
    private static void AssertLetGo(NodeHome home)
    {
        Assert.NotEmpty(ReadAlone(home.DatabasePath));
        IReadOnlyDictionary<string, string> disposed = RecordHomes.Files(home.Path);
        GC.WaitForPendingFinalizers();
        Assert.Equal(disposed, RecordHomes.Files(home.Path));
    }

    private static byte[] ReadAlone(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        byte[] content = new byte[file.Length];
        file.ReadExactly(content);
        return content;
    }
}
