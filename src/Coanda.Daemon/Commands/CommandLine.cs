namespace Coanda.Daemon.Commands;

/// <summary>
/// One external command for an <see cref="ICommandRunner"/> to run: the program, its arguments, the
/// directory it runs in, and the variables it is given on top of the daemon's own environment. The
/// arguments are either a list, each passed to the program as one argument, or, when
/// <see cref="VerbatimArguments"/> is set, one string passed exactly as written, which is how
/// <c>cmd.exe</c> is handed a command line.
/// </summary>
public sealed record CommandLine(
    string Program,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>The argument string passed exactly as written instead of <see cref="Arguments"/>, or null to pass the list.</summary>
    public string? VerbatimArguments { get; init; }

    /// <summary>
    /// How long the command may run before the runner ends it, with every process it started that is
    /// still its descendant, and answers <see cref="CommandResult.TimedOut"/>; null for no limit
    /// beyond the caller's cancellation.
    /// </summary>
    public TimeSpan? Limit { get; init; }

    /// <summary>
    /// The command line <paramref name="line"/> run through the platform's shell in
    /// <paramref name="workingDirectory"/>: <c>/bin/sh -c</c> on macOS and Linux, and on Windows
    /// <c>cmd.exe</c> with the verbatim argument string <c>/d /s /c "&lt;line&gt;"</c>, so the line
    /// reaches the shell as written.
    /// </summary>
    public static CommandLine Shell(string line, string workingDirectory) =>
        OperatingSystem.IsWindows()
            ? new CommandLine("cmd.exe", [], workingDirectory, NoVariables) { VerbatimArguments = $"/d /s /c \"{line}\"" }
            : new CommandLine("/bin/sh", ["-c", line], workingDirectory, NoVariables);

    /// <summary>No variables beyond the daemon's own environment.</summary>
    public static IReadOnlyDictionary<string, string> NoVariables { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The command as a person would type it, for a message; an argument holding a space is quoted.</summary>
    public string Display =>
        VerbatimArguments is { } verbatim
            ? $"{Program} {verbatim}"
            : string.Join(' ', [Program, .. Arguments.Select(Quote)]);

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace)
            ? $"\"{argument}\""
            : argument;
}
