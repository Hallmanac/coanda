using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Sdk.Repositories;

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

    /// <summary>
    /// Where a daemon the CLI starts detached prints whatever does not reach its log, such as a
    /// failure before the log is open. Each start appends to it, and it is kept after the start.
    /// </summary>
    public string DaemonOutputPath => System.IO.Path.Combine(Path, "daemon.out");

    /// <summary>The node configuration <see cref="Api.NodeConfiguration"/> reads when <c>coa daemon run</c> starts; it may be absent.</summary>
    public string ConfigurationPath => System.IO.Path.Combine(Path, "node.json");

    /// <summary>
    /// The node's identity, its node id and its ed25519 key pair (<see cref="NodeIdentity"/>), made at
    /// the daemon's first start and never rewritten. Kept apart from <see cref="ConfigurationPath"/>,
    /// which is the user's to edit.
    /// </summary>
    public string NodeIdentityPath => System.IO.Path.Combine(Path, "node-identity.json");

    /// <summary>The empty file a start holds an exclusive lock on while it makes the node identity, so two starts at once make one.</summary>
    public string NodeIdentityLockPath => System.IO.Path.Combine(Path, "node-identity.lock");

    /// <summary>The files SQLite keeps beside the database: the write-ahead log, the shared-memory file, and the rollback journal.</summary>
    public IReadOnlyList<string> DatabaseCompanionPaths => [DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal"];

    /// <summary>
    /// The file a daemon holds locked from the start of its start until it stops, and a record export or
    /// rebuild holds while it runs (<see cref="HomeLock"/>). It holds nothing.
    /// </summary>
    public string LockPath => System.IO.Path.Combine(Path, "home.lock");

    /// <summary>Every file in the home that only its owner may read or write.</summary>
    public IReadOnlyList<string> OwnerOnlyPaths => [DaemonInfoPath, DatabasePath, .. DatabaseCompanionPaths, LogPath, DaemonOutputPath, NodeIdentityPath, LockPath];

    /// <summary>
    /// The module area: <c>modules-private/</c> under the home, holding one directory per module, each
    /// owner-only. A module's directory holds its credential profiles, one entry each, named by the
    /// profile, with the secret material inside, which the core never reads. Everything under it is
    /// created owner-only and narrowed to that at every start.
    /// </summary>
    public string ModuleAreaPath => System.IO.Path.Combine(Path, "modules-private");

    /// <summary>
    /// The module's own area, <c>modules-private/&lt;module&gt;</c> under the home: the entries directly
    /// inside it are the credential profiles the node holds for the module. The module must be named by
    /// <see cref="ModuleNames.IsModuleName"/>, so the path never leaves the module area.
    /// </summary>
    public string ModuleAreaOf(string module) =>
        System.IO.Path.Combine(ModuleAreaPath, ModuleNames.Require(module));

    /// <summary>
    /// Where a flow run's agent steps work: <c>work/&lt;project&gt;/&lt;flow run id&gt;</c> under the home.
    /// This is the one rule that places a flow run's work path: a dispatch carries the path it gives, and
    /// the core's API shows it on a flow run (<see cref="WorkPathOf"/>), so a module never computes it.
    /// The project must be a plain directory name, so the path never leaves <c>work/</c>.
    /// </summary>
    public string FlowRunWorkPath(string project, Guid flowRunId) =>
        System.IO.Path.Combine(WorkPath, RequirePlain(project, "working directory"), flowRunId.ToString());

    /// <summary>The flow run's work path (<see cref="FlowRunWorkPath"/>), or null when its project's name is not a plain directory name and so gives it none.</summary>
    public string? WorkPathOf(string project, Guid flowRunId) =>
        RepoLayout.IsPlain(project)
            ? FlowRunWorkPath(project, flowRunId)
            : null;

    /// <summary>The directory holding one directory per project of its flow runs' work paths.</summary>
    public string WorkPath => System.IO.Path.Combine(Path, "work");

    /// <summary>
    /// The core's bare clone of the repo <paramref name="repo"/> of <paramref name="project"/>:
    /// <c>projects/&lt;project&gt;/repos/&lt;repo&gt;</c> under the home, as <see cref="RepoLayout"/> places it.
    /// </summary>
    public string RepoClonePath(string project, string repo) => RepoLayout.ClonePath(Path, project, repo);

    /// <summary>
    /// Where a build from before the per-repo layout kept a project's one clone, <c>clones/&lt;project&gt;/repo.git</c>
    /// under the home; the start moves each to <see cref="RepoClonePath"/>.
    /// </summary>
    public string EarlierClonesPath => System.IO.Path.Combine(Path, "clones");

    /// <summary>
    /// Where the transcripts of a flow run's sessions are kept: <c>transcripts/&lt;project&gt;/&lt;flow run id&gt;</c>
    /// under the home, outside the flow run's work directory, so a session never finds them among its
    /// files. The project must be a plain directory name, as for <see cref="FlowRunWorkPath"/>.
    /// </summary>
    public string FlowRunTranscriptPath(string project, Guid flowRunId) =>
        System.IO.Path.Combine(Path, "transcripts", RequirePlain(project, "transcript directory"), flowRunId.ToString());

    /// <summary>
    /// Where an executor keeps what it needs to find its sessions again after a daemon restart:
    /// <c>sessions/</c> under the home, one record per live session beside the files its processes
    /// read their input from and write their error output to.
    /// </summary>
    public string SessionsPath => System.IO.Path.Combine(Path, "sessions");

    /// <summary>The record of session <paramref name="sessionId"/> in <see cref="SessionsPath"/>.</summary>
    public string SessionRecordPath(Guid sessionId) => System.IO.Path.Combine(SessionsPath, $"{sessionId}.json");

    /// <summary>
    /// The transcript directory of the flow run whose work directory is <paramref name="flowRunWorkPath"/>,
    /// a path <see cref="FlowRunWorkPath"/> returned. An executor is handed the work directory alone,
    /// and this is how it finds where the flow run's transcripts go.
    /// </summary>
    public string TranscriptDirectoryOf(string flowRunWorkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowRunWorkPath);
        string relative = System.IO.Path.GetRelativePath(WorkPath, System.IO.Path.GetFullPath(flowRunWorkPath));
        string[] parts = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar]);
        return parts is [string project, string flowRun] && Guid.TryParse(flowRun, out Guid flowRunId)
            ? FlowRunTranscriptPath(project, flowRunId)
            : throw new DomainException($"{flowRunWorkPath} is not a flow run's working directory under the node home {Path}, so it has no transcript directory.");
    }

    /// <summary>The project's name, when it is a plain directory name (<see cref="RepoLayout.IsPlain"/>), which is what gives its flow runs work paths and transcript directories under the home.</summary>
    private static string RequirePlain(string project, string what) =>
        RepoLayout.IsPlain(project)
            ? project
            : throw new DomainException($"Project '{project}' is not a plain directory name, so its flow runs have no {what} under the node home.");

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
    /// inherits, and on every platform the token file, the database and its companion files, the
    /// log, the detached daemon's output, and the node identity, which it never creates. It also
    /// creates the module area owner-only and narrows every directory and file under it, the area
    /// included, to owner-only (<see cref="OwnerOnly.NarrowTree"/>).
    /// Called before anything opens the database or writes to any of those files.
    /// </summary>
    public NodeHome EnsureExists()
    {
        OwnerOnly.EnsureDirectory(Path);
        foreach (string path in OwnerOnlyPaths)
        {
            OwnerOnly.Narrow(path);
        }

        OwnerOnly.NarrowTree(ModuleAreaPath);
        return this;
    }

    /// <summary>The module's own area (<see cref="ModuleAreaOf"/>), created owner-only when it is absent.</summary>
    public string EnsureModuleArea(string module)
    {
        string area = ModuleAreaOf(module);
        OwnerOnly.EnsurePrivateDirectory(ModuleAreaPath);
        OwnerOnly.EnsurePrivateDirectory(area);
        return area;
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
