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
