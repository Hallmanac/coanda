using Coanda.Core;

namespace Coanda.Daemon;

/// <summary>
/// Where a node keeps everything: one SQLite database, the running daemon's address, and its log.
/// Defaults to <c>~/.coanda</c>; the <c>COANDA_HOME</c> variable overrides it, which is how tests
/// and a second node on one machine keep apart.
/// </summary>
public sealed record NodeHome(string Path)
{
    public const string EnvironmentVariable = "COANDA_HOME";

    public string DatabasePath => System.IO.Path.Combine(Path, "coanda.db");
    public string DaemonInfoPath => System.IO.Path.Combine(Path, "daemon.json");
    public string LogPath => System.IO.Path.Combine(Path, "coanda.log");

    /// <summary>The files SQLite keeps beside the database: the write-ahead log, the shared-memory file, and the rollback journal.</summary>
    public IReadOnlyList<string> DatabaseCompanionPaths => [DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal"];

    /// <summary>Every file in the home that only its owner may read or write.</summary>
    public IReadOnlyList<string> OwnerOnlyPaths => [DaemonInfoPath, DatabasePath, .. DatabaseCompanionPaths, LogPath];

    /// <summary>
    /// Where a flow's judgments work: <c>work/&lt;project&gt;/&lt;flow id&gt;</c> under the home.
    /// The project must be a plain directory name, so the path never leaves <c>work/</c>.
    /// </summary>
    public string FlowWorkPath(string project, Guid flowId)
    {
        bool plain = project is not ("" or "." or "..")
            && project.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
            && project.IndexOfAny(['/', '\\']) < 0;
        return plain
            ? System.IO.Path.Combine(Path, "work", project, flowId.ToString())
            : throw new DomainException($"Project '{project}' is not a plain directory name, so its flows have no working directory under the node home.");
    }

    public static NodeHome Resolve()
    {
        string? overridden = Environment.GetEnvironmentVariable(EnvironmentVariable);
        string path = string.IsNullOrWhiteSpace(overridden)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coanda")
            : overridden;
        return new NodeHome(System.IO.Path.GetFullPath(path));
    }

    /// <summary>
    /// Creates the home if it is absent and narrows to owner-only access anything an earlier run
    /// left wider: on Windows the directory itself, whose access list every file created in it
    /// inherits, and on every platform the token file, the database and its companion files, and
    /// the log. Called before anything opens the database or writes to any of those files.
    /// </summary>
    public NodeHome EnsureExists()
    {
        OwnerOnly.EnsureDirectory(Path);
        foreach (string path in OwnerOnlyPaths)
        {
            OwnerOnly.Narrow(path);
        }

        return this;
    }

    /// <summary>
    /// Creates the database empty and owner-only when it is absent, so SQLite opens a file that
    /// already has the access it should, and on macOS and Linux gives the companion files it
    /// creates the same mode. SQLite reads a zero-length file as an empty database.
    /// </summary>
    public NodeHome EnsureDatabase()
    {
        using FileStream database = OwnerOnly.Open(DatabasePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        return this;
    }
}
