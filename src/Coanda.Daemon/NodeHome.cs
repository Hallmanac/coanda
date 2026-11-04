using Coanda.Core;

namespace Coanda.Daemon;

/// <summary>
/// Where a node keeps everything: one SQLite database, the running daemon's address, its log, and
/// its configuration.
/// Defaults to <c>~/.coanda</c>; the <c>COANDA_HOME</c> variable overrides it, which is how tests
/// and a second node on one machine keep apart.
/// </summary>
public sealed record NodeHome(string Path)
{
    public const string EnvironmentVariable = "COANDA_HOME";

    public string DatabasePath => System.IO.Path.Combine(Path, "coanda.db");
    public string DaemonInfoPath => System.IO.Path.Combine(Path, "daemon.json");
    public string LogPath => System.IO.Path.Combine(Path, "coanda.log");

    /// <summary>The node configuration <see cref="Api.NodeConfiguration"/> reads when <c>coa daemon run</c> starts; it may be absent.</summary>
    public string ConfigurationPath => System.IO.Path.Combine(Path, "node.json");

    /// <summary>The files SQLite keeps beside the database: the write-ahead log, the shared-memory file, and the rollback journal.</summary>
    public IReadOnlyList<string> DatabaseCompanionPaths => [DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal"];

    /// <summary>Every file in the home that only its owner may read or write.</summary>
    public IReadOnlyList<string> OwnerOnlyPaths => [DaemonInfoPath, DatabasePath, .. DatabaseCompanionPaths, LogPath];

    /// <summary>
    /// Where a flow's judgments work: <c>work/&lt;project&gt;/&lt;flow id&gt;</c> under the home.
    /// The project must be a plain directory name, so the path never leaves <c>work/</c>.
    /// </summary>
    public string FlowWorkPath(string project, Guid flowId) =>
        System.IO.Path.Combine(Path, "work", RequirePlain(project, "working directory"), flowId.ToString());

    /// <summary>The directory holding one directory per project whose flows have needed the node's clone of its repository.</summary>
    public string ClonesPath => System.IO.Path.Combine(Path, "clones");

    /// <summary>
    /// The node's one clone of the project's repository, which every flow's worktree in the project
    /// is cut from: <c>clones/&lt;project&gt;/repo.git</c> under the home. The project must be a plain
    /// directory name, as for <see cref="FlowWorkPath"/>.
    /// </summary>
    public string ProjectClonePath(string project) =>
        System.IO.Path.Combine(ClonesPath, RequirePlain(project, "clone of its repository"), "repo.git");

    /// <summary>
    /// Where the project's clone is made before it is moved to <see cref="ProjectClonePath"/>, beside
    /// it, so a clone ended part way never sits at the clone's path.
    /// </summary>
    public string ProjectClonePartialPath(string project) => ProjectClonePath(project) + ".partial";

    /// <summary>
    /// Where the transcripts of a flow's sessions are kept: <c>transcripts/&lt;project&gt;/&lt;flow id&gt;</c>
    /// under the home, outside the flow's work directory, so a session never finds them among its
    /// files. The project must be a plain directory name, as for <see cref="FlowWorkPath"/>.
    /// </summary>
    public string FlowTranscriptPath(string project, Guid flowId) =>
        System.IO.Path.Combine(Path, "transcripts", RequirePlain(project, "transcript directory"), flowId.ToString());

    /// <summary>
    /// Where an executor keeps what it needs to find its sessions again after a daemon restart:
    /// <c>sessions/</c> under the home, one record per live session beside the files its processes
    /// read their input from and write their error output to.
    /// </summary>
    public string SessionsPath => System.IO.Path.Combine(Path, "sessions");

    /// <summary>The record of session <paramref name="sessionId"/> in <see cref="SessionsPath"/>.</summary>
    public string SessionRecordPath(Guid sessionId) => System.IO.Path.Combine(SessionsPath, $"{sessionId}.json");

    /// <summary>
    /// The transcript directory of the flow whose work directory is <paramref name="flowWorkPath"/>,
    /// a path <see cref="FlowWorkPath"/> returned. An executor is handed the work directory alone,
    /// and this is how it finds where the flow's transcripts go.
    /// </summary>
    public string TranscriptDirectoryOf(string flowWorkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowWorkPath);
        string relative = System.IO.Path.GetRelativePath(System.IO.Path.Combine(Path, "work"), System.IO.Path.GetFullPath(flowWorkPath));
        string[] parts = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar]);
        return parts is [string project, string flow] && Guid.TryParse(flow, out Guid flowId)
            ? FlowTranscriptPath(project, flowId)
            : throw new DomainException($"{flowWorkPath} is not a flow's working directory under the node home {Path}, so it has no transcript directory.");
    }

    /// <summary>Whether the project's name is a plain directory name, which is what gives its flows work paths and a clone under the home.</summary>
    public static bool IsPlain(string project) =>
        project is not ("" or "." or "..")
            && project.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
            && project.IndexOfAny(['/', '\\']) < 0;

    private static string RequirePlain(string project, string what) =>
        IsPlain(project)
            ? project
            : throw new DomainException($"Project '{project}' is not a plain directory name, so its flows have no {what} under the node home.");

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
    /// <remarks>
    /// A database that already exists is never opened here: <see cref="EnsureExists"/> has already
    /// narrowed it by path. Opening it would take .NET's advisory lock on the file, which on macOS
    /// conflicts with an exclusive lock a SQLite connection may still hold on it, and fails as a
    /// file in use. A database something else creates between the check and the create is
    /// narrowed by path the same way.
    /// </remarks>
    public NodeHome EnsureDatabase()
    {
        if (File.Exists(DatabasePath))
        {
            return this;
        }

        try
        {
            using FileStream created = OwnerOnly.Open(DatabasePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException) when (File.Exists(DatabasePath))
        {
            OwnerOnly.Narrow(DatabasePath);
        }

        return this;
    }
}
