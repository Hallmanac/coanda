namespace Coanda.Sdk.Commands;

/// <summary>
/// How a command ended: its exit code, what it wrote to its standard output and to its standard
/// error, and both together in the order they were read.
/// </summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, string Combined)
{
    /// <summary>The exit code a result carries when the command was ended for running past its <see cref="CommandLine.Limit"/>.</summary>
    public const int TimedOutExitCode = -1;

    /// <summary>Whether the command exited with code zero within its limit.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>
    /// Whether the command ran past its <see cref="CommandLine.Limit"/> and was ended, with every
    /// process it started that was still its descendant; the output is what it wrote until then.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Whether the command wrote more than the runner keeps of any of the three texts, so that text
    /// holds only the end of what was written.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>A result for a command ended for running past its limit, carrying what it wrote until then.</summary>
    public static CommandResult LimitReached(string standardOutput, string standardError, string combined) =>
        new(TimedOutExitCode, standardOutput, standardError, combined) { TimedOut = true };
}
