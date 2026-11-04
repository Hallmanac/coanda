namespace Coanda.Daemon.Repositories;

/// <summary>
/// A path with every symbolic link along it resolved, so a path git prints (which has its links
/// resolved, as <c>/private/var</c> for <c>/var</c> on macOS) compares equal to the path the node
/// home builds. A part of the path that does not exist is kept as written.
/// </summary>
public static class RealPath
{
    private const int MaximumLinks = 40;

    public static string Of(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (int followed = 0; followed < MaximumLinks; followed++)
        {
            string? link = FirstLink(full);
            if (link is null)
            {
                return full;
            }

            FileSystemInfo info = new FileInfo(link);
            string target = info.LinkTarget ?? link;
            string resolved = Path.GetFullPath(target, Path.GetDirectoryName(link) ?? link);
            string rest = Path.GetRelativePath(link, full);
            full = Path.TrimEndingDirectorySeparator(rest == "." ? resolved : Path.Join(resolved, rest));
        }

        return full;
    }

    /// <summary>Whether two paths name the same place once their links are resolved.</summary>
    public static bool Same(string one, string other) =>
        string.Equals(Of(one), Of(other), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The shortest prefix of <paramref name="full"/> that is a symbolic link, or null when none is.</summary>
    private static string? FirstLink(string full)
    {
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        foreach (string part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, part);
            var info = new FileInfo(current);
            if (!info.Exists && !Directory.Exists(current))
            {
                return null;
            }

            if (info.LinkTarget is not null)
            {
                return current;
            }
        }

        return null;
    }
}
