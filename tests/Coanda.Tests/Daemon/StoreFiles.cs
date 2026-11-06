using Coanda.Daemon;

namespace Coanda.Tests.Daemon;

/// <summary>Reads every file of a home's store whole, for a test showing what the store wrote and what it did not.</summary>
internal static class StoreFiles
{
    /// <summary>
    /// Closes the store with <paramref name="close"/>, then reads each of its files that is left.
    /// </summary>
    /// <remarks>
    /// Never while the store is open. On macOS the whole-file lock a read takes and the byte-range locks
    /// SQLite takes on the record and its shared-memory file refuse each other, so a read is refused whenever
    /// it meets a write, a checkpoint, or another connection's open. And closing the read's own handle drops
    /// every lock SQLite holds on that file in this process, which the store still relies on.
    /// <para>
    /// What this reads is narrower than the store's whole history. When the last connection closes, SQLite
    /// copies the newest version of each page from the write-ahead log into the record and deletes the log, so
    /// a value written and then overwritten before the store closed may have sat in an older log frame that
    /// never reaches these files. A test showing a value is never written can rely on this only for values
    /// that stay in place until the store closes.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<StoreFile>> ReadOnceClosedAsync(NodeHome home, Func<ValueTask> close)
    {
        await close();
        return [.. new[] { home.DatabasePath }.Concat(home.DatabaseCompanionPaths).Where(File.Exists).Select(path => new StoreFile(path, ReadShared(path)))];
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>One file of a store, with what it held when it was read.</summary>
internal sealed record StoreFile(string Path, byte[] Content);
