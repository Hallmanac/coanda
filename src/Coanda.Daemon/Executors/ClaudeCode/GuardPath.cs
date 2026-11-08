using System.Text.RegularExpressions;

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
public static partial class GuardPath
{
    /// <summary>How many links one resolution follows before it gives up, as a loop of links would never end.</summary>
    private const int MostLinks = 40;

    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>The comparer that matches <see cref="Comparison"/>.</summary>
    public static StringComparer Comparer { get; } = StringComparer.FromComparison(Comparison);

    /// <summary>
    /// <paramref name="path"/> resolved against <paramref name="baseDirectory"/> when it is relative,
    /// then with its links resolved as far as it exists, and with no separator at its end unless it is a root.
    /// </summary>
    public static string Resolve(string path, string baseDirectory)
    {
        int links = 0;
        return Real(path, baseDirectory, ref links);
    }

    /// <summary>
    /// The paths a text may name. On Windows that is the text as Windows reads it and, when it is in
    /// the form Git Bash gives a drive (<c>/c/Users/me</c> or <c>/cygdrive/c/Users/me</c>), the drive
    /// path the shell reads it as; elsewhere it is the text alone.
    /// </summary>
    public static IReadOnlyList<string> Readings(string text) =>
        OperatingSystem.IsWindows() && ShellDrivePath(text) is { } drive
            ? [text, drive]
            : [text];

    /// <summary>
    /// The Windows path a Git Bash drive path names, such as <c>C:\Users\me</c> for <c>/c/Users/me</c>
    /// or <c>/cygdrive/c/Users/me</c>; null for any other text.
    /// </summary>
    public static string? ShellDrivePath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Match match = ShellDrive().Match(text);
        return match.Success
            ? $"{char.ToUpperInvariant(match.Groups["drive"].Value[0])}:\\{match.Groups["rest"].Value.TrimStart('/', '\\').Replace('/', '\\')}"
            : null;
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

    /// <summary>
    /// <paramref name="path"/> resolved against <paramref name="baseDirectory"/>. On Windows a path
    /// rooted with no drive, such as <c>\Users\me</c>, is on the base directory's drive, never on the
    /// drive of this process's current directory, so where the guard runs never changes what a call reaches.
    /// </summary>
    private static string Real(string path, string baseDirectory, ref int links)
    {
        string full = OperatingSystem.IsWindows()
            ? Path.GetFullPath(path, Path.GetFullPath(baseDirectory))
            : Path.Combine(baseDirectory, path);
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

                current = Real(target, current, ref links);
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

    [GeneratedRegex(@"^[/\\](?:cygdrive[/\\])?(?<drive>[A-Za-z])(?<rest>(?:[/\\].*)?)$", RegexOptions.Singleline)]
    private static partial Regex ShellDrive();
}
