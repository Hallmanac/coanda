namespace Coanda.Sdk.Repositories;

/// <summary>
/// Finds Git for Windows' real git executable behind the launcher on a PATH. The <c>git.exe</c> in
/// an install's <c>cmd</c> and <c>bin</c> directories is a small launcher that starts the real git
/// and stays running until it exits, which on the Windows build the node runs leaves one kernel
/// access token allocated until reboot for each call; starting the real executable directly
/// avoids that. What git itself starts during a call (the shell that runs the ssh command, the
/// credential helper) is not affected.
/// </summary>
/// <remarks>
/// The launcher is looked up once, from the PATH value the instance is given, the first time it is
/// asked. The real executable found from it is reused while it still exists; once it no longer
/// does, or while none has been found, each call looks again from the launcher's install, so an
/// update of Git for Windows is followed without a restart.
/// </remarks>
internal sealed class GitExecutable
{
    /// <summary>The flavours of an install whose <c>bin</c> directory holds the real git, in the order they are tried.</summary>
    public static readonly IReadOnlyList<string> Flavours = ["ucrt64", "mingw64", "clangarm64"];

    private const string ExecutableName = "git.exe";

    private readonly Lazy<string?> install;

    private volatile string? found;

    internal GitExecutable(Func<string?> pathValue) =>
        install = new Lazy<string?>(() => InstallOf(pathValue()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The one resolution for this process: from the daemon's own PATH on Windows, and on any other
    /// operating system none, so it always answers null there.
    /// </summary>
    public static GitExecutable ForProcess { get; } = new(
        () => OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("PATH")
            : null);

    /// <summary>The real git executable to start in place of git by name, or null to start git by name.</summary>
    public string? Current()
    {
        if (install.Value is not { } root)
        {
            return null;
        }

        string? held = found;
        if (held is not null && File.Exists(held))
        {
            return held;
        }

        string? real = RealIn(root);
        found = real;
        return real;
    }

    /// <summary>
    /// The resolution rule on one PATH value: the real git behind Git for Windows' launcher when the
    /// first <c>git.exe</c> on <paramref name="pathValue"/> is one, and null in every other case.
    /// </summary>
    public static string? Resolve(string? pathValue) =>
        InstallOf(pathValue) is { } root
            ? RealIn(root)
            : null;

    /// <summary>
    /// Where the launcher that is the first <c>git.exe</c> on <paramref name="pathValue"/> was
    /// installed: its directory's parent, when its directory is named <c>cmd</c> or <c>bin</c> in
    /// any case. Null when there is no <c>git.exe</c> on it or the first one sits in neither. Whether
    /// the install holds a real git is asked of it at each lookup, not here, so a real git that
    /// appears there later is found.
    /// </summary>
    private static string? InstallOf(string? pathValue) =>
        FirstOnPath(pathValue) is { } first
        && Path.GetDirectoryName(first) is { } directory
        && IsLauncherDirectory(Path.GetFileName(directory))
            ? Path.GetDirectoryName(directory)
            : null;

    private static bool IsLauncherDirectory(string name) =>
        string.Equals(name, "cmd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase);

    private static string? FirstOnPath(string? pathValue)
    {
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (string entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = entry.Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory, ExecutableName));
            }
            catch (Exception unusable) when (unusable is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? RealIn(string root) =>
        Flavours
            .Select(flavour => Path.Combine(root, flavour, "bin", ExecutableName))
            .FirstOrDefault(File.Exists);
}
