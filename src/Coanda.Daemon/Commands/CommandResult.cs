namespace Coanda.Daemon.Commands;

/// <summary>
/// How a command ended: its exit code, what it wrote to its standard output and to its standard
/// error, and both together in the order they were read.
/// </summary>
public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, string Combined)
{
    public bool Succeeded => ExitCode == 0;
}
