using Coanda.Sdk.Commands;

namespace Coanda.Sdk.Repositories;

/// <summary>
/// Builds and runs the git commands the worktree system step and a worktree's removal run, through the
/// command runner. Every one runs with <c>GIT_TERMINAL_PROMPT=0</c> and with ssh in batch mode, so
/// none waits on a prompt for credentials; the runner closes its standard input besides. Every one
/// is also ended once it has run for <see cref="Limit"/>, so a fetch from a remote that stops
/// answering cannot hold the step, or the clone's turn at git, without end.
/// </summary>
public sealed class Git(ICommandRunner runner)
{
    /// <summary>The name the node's clone gives the project's remote.</summary>
    public const string Remote = "origin";

    /// <summary>How many lines of a failing command's error output a failure quotes.</summary>
    public const int QuotedLines = 20;

    /// <summary>How long one git command may run before it is ended and fails.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The variables every git command is given. <c>GIT_SSH_COMMAND</c> takes precedence over
    /// <c>core.sshCommand</c> and <c>GIT_SSH</c>, so it overrides any of those the operator set;
    /// ssh's own configuration still applies. <c>LC_ALL=C</c> keeps git's output untranslated, so
    /// what the daemon reads from it, such as a worktree's <c>initializing</c> lock reason, reads the
    /// same under any locale.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes",
        ["LC_ALL"] = "C",
    };

    /// <summary>A git command against the repository at <paramref name="gitDirectory"/>, run from its parent directory.</summary>
    public static CommandLine Against(string gitDirectory, params string[] arguments) =>
        new("git", [$"--git-dir={gitDirectory}", .. arguments], System.IO.Path.GetDirectoryName(gitDirectory) ?? gitDirectory, Environment) { Limit = Limit };

    /// <summary>A git command run in <paramref name="workingDirectory"/>, finding its repository from there.</summary>
    public static CommandLine In(string workingDirectory, params string[] arguments) =>
        new("git", arguments, workingDirectory, Environment) { Limit = Limit };

    /// <summary>
    /// Runs the command and returns its standard output; a nonzero exit, or output longer than the
    /// runner keeps, so that what is returned would be only its end, throws <see cref="GitCommandException"/>.
    /// </summary>
    public async Task<string> RunAsync(CommandLine command, CancellationToken cancellationToken)
    {
        CommandResult result = await runner.RunAsync(command, cancellationToken);
        return result is { Succeeded: true, Truncated: false }
            ? result.StandardOutput
            : throw new GitCommandException(command, result);
    }

    /// <summary>Runs the command and returns how it ended, whatever its exit code.</summary>
    public Task<CommandResult> TryRunAsync(CommandLine command, CancellationToken cancellationToken) =>
        runner.RunAsync(command, cancellationToken);
}

/// <summary>
/// A git command exited nonzero, or wrote more output than the runner keeps. Its message names the
/// command and, for a nonzero exit, quotes at most the last
/// <see cref="Git.QuotedLines"/> lines of its error output, with the user information of any URL
/// removed, because it becomes a pause reason stored on the flow run.
/// </summary>
public sealed class GitCommandException(CommandLine command, CommandResult result)
    : Exception(Describe(command, result))
{
    private static string Describe(CommandLine command, CommandResult result)
    {
        if (result.Succeeded)
        {
            return OutputText.Redact($"{command.Display} wrote more output than the daemon keeps of a command, so what it wrote cannot be read in full.");
        }

        IReadOnlyList<string> lines = OutputText.LastLines(result.StandardError, Git.QuotedLines);
        string quoted = lines.Count == 0
            ? "it wrote no error output."
            : $"its error output ends:\n{string.Join('\n', lines)}";
        string ended = result.TimedOut && command.Limit is { } limit
            ? $"was still running after {Durations.Describe(limit)} and was ended"
            : $"exited with code {result.ExitCode}";
        return OutputText.Redact($"{command.Display} {ended}; {quoted}");
    }
}
