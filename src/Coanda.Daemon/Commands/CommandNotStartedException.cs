namespace Coanda.Daemon.Commands;

/// <summary>A command could not be started at all: its program does not exist or cannot run, or the runner starts nothing.</summary>
public sealed class CommandNotStartedException(CommandLine command, string why, Exception? inner = null)
    : Exception($"{OutputText.Redact(command.Program)} could not be started: {why}", inner)
{
    public CommandLine Command { get; } = command;
}
