namespace Coanda.Daemon.Executors.ClaudeCode;

/// <summary>
/// Finds the claude executable a session's processes start: the configured path when it names a
/// directory, otherwise the first match for the bare name on the PATH of the environment the
/// process starts with, trying each PATHEXT extension on Windows. A command script (<c>.cmd</c> or
/// <c>.bat</c>) is refused, because starting one runs a command interpreter, which would read the
/// arguments under its own quoting rules; only a native executable is started.
/// </summary>
public static class ClaudeExecutable
{
    private static readonly string[] CommandScripts = [".cmd", ".bat"];

    public static string Resolve(string configured, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configured);
        ArgumentNullException.ThrowIfNull(environment);
        string resolved = NamesDirectory(configured)
            ? Path.GetFullPath(configured)
            : OnPath(configured, environment)
                ?? throw new InvalidOperationException(
                    $"The claude executable '{configured}' could not be started: no file of that name is on the PATH. Install Claude Code or configure the executable's path in node.json.");
        return IsCommandScript(resolved)
            ? throw new InvalidOperationException(
                $"The claude executable '{resolved}' is a command script, which runs through a command interpreter, so it was not started. Configure the path of the native claude executable instead.")
            : resolved;
    }

    private static bool NamesDirectory(string path) =>
        Path.IsPathRooted(path) || path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar);

    private static bool IsCommandScript(string path) =>
        CommandScripts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string? OnPath(string name, IReadOnlyDictionary<string, string> environment)
    {
        string path = Lookup(environment, "PATH") ?? string.Empty;
        IReadOnlyList<string> candidates = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? [.. (Lookup(environment, "PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(extension => name + extension)]
            : [name];
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(directory => candidates.Select(candidate => Path.Combine(directory, candidate)))
            .FirstOrDefault(File.Exists);
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> environment, string name) =>
        environment.FirstOrDefault(entry => string.Equals(entry.Key, name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;
}
