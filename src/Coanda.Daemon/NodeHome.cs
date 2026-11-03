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

    /// <summary>
    /// Where a flight's judgments work: <c>work/&lt;project&gt;/&lt;flight id&gt;</c> under the home.
    /// The project must be a plain directory name, so the path never leaves <c>work/</c>.
    /// </summary>
    public string FlightWorkPath(string project, Guid flightId)
    {
        bool plain = project is not ("" or "." or "..")
            && project.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
            && project.IndexOfAny(['/', '\\']) < 0;
        return plain
            ? System.IO.Path.Combine(Path, "work", project, flightId.ToString())
            : throw new DomainException($"Project '{project}' is not a plain directory name, so its flights have no working directory under the node home.");
    }

    public static NodeHome Resolve()
    {
        string? overridden = Environment.GetEnvironmentVariable(EnvironmentVariable);
        string path = string.IsNullOrWhiteSpace(overridden)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".coanda")
            : overridden;
        return new NodeHome(System.IO.Path.GetFullPath(path));
    }

    public NodeHome EnsureExists()
    {
        Directory.CreateDirectory(Path);
        return this;
    }
}
