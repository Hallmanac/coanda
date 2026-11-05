namespace Coanda.Sdk.Commands;

/// <summary>
/// The runner a daemon host uses when its options supply none: it starts nothing and fails every
/// command as one that cannot be started, so a system step that needs a command pauses rather than
/// acting on the host it happens to run on. <c>coa daemon run</c> supplies the real runner.
/// </summary>
public sealed class NoCommandRunner : ICommandRunner
{
    public Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
        Task.FromException<CommandResult>(new CommandNotStartedException(
            command, "this daemon was started with no command runner, so it starts no external command."));
}
