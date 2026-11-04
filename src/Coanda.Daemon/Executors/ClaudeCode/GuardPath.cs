namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// How the tool guard turns a path into the form it compares: absolute, with every symbolic link in
/// the part of it that exists resolved, and compared without regard to case on Windows and macOS.
/// </summary>
/// <remarks>
/// On macOS and Linux a <c>..</c> after a symbolic link climbs from the link's target, as the
/// operating system does, so the components are walked one at a time rather than collapsed first.
/// Windows collapses <c>..</c> by name before it follows any link, so there the path is made full
/// first, which also turns either separator into the one Windows uses.
/// </remarks>
public static class GuardPath
{
    /// <summary>How many links one resolution follows before it gives up, as a loop of links would never end.</summary>
    private const int MostLinks = 40;

    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// <paramref name="path"/> resolved against <paramref name="baseDirectory"/> when it is relative,
    /// then with its links resolved as far as it exists, and with no separator at its end unless it is a root.
    /// </summary>
    public static string Resolve(string path, string baseDirectory)
    {
        string rooted = path.Length == 0
            ? baseDirectory
            : Path.Combine(baseDirectory, path);
        int links = 0;
        return Real(rooted, ref links);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or inside it; both already resolved.</summary>
    public static bool IsWithin(string path, string directory)
    {
        if (string.Equals(path, directory, Comparison))
        {
            return true;
        }

        string prefix = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }

    private static string Real(string path, ref int links)
    {
        string full = OperatingSystem.IsWindows()
            ? Path.GetFullPath(path)
            : path;
        string root = Path.GetPathRoot(full) is { Length: > 0 } found
            ? found
            : throw new ArgumentException($"The path '{path}' is not rooted.", nameof(path));
        string[] parts = full[root.Length..].Split(
            OperatingSystem.IsWindows() ? ['\\', '/'] : ['/'],
            StringSplitOptions.RemoveEmptyEntries);

        string current = root;
        bool exists = true;
        foreach (string part in parts)
        {
            switch (part)
            {
                case ".":
                    continue;
                case "..":
                    current = Path.GetDirectoryName(current) ?? current;
                    continue;
            }

            string next = Path.Combine(current, part);
            if (!exists)
            {
                current = next;
                continue;
            }

            var entry = new FileInfo(next);
            if (entry.LinkTarget is { } target)
            {
                if (++links > MostLinks)
                {
                    throw new IOException($"The path '{path}' follows more than {MostLinks} symbolic links.");
                }

                current = Real(Path.Combine(current, target), ref links);
            }
            else
            {
                exists = entry.Exists || Directory.Exists(next);
                current = next;
            }
        }

        // A root keeps its separator.
        return Path.TrimEndingDirectorySeparator(current);
    }
}
