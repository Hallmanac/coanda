namespace Coanda.Daemon.Commands;

/// <summary>
/// The one seam every external command the daemon's system steps run goes through: the worktree, gates,
/// and open-pr system steps (git, and gh for the GitHub token) and a worktree's removal. A runner closes
/// the command's standard input, so nothing it starts waits on input, and logs none of its output,
/// because a command run through it may print a credential. Cancelling the token ends the command
/// and every process it started that is still its descendant; one that detached from its process
/// tree is not ended.
/// </summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> to its exit and returns how it ended, whatever its exit code.
    /// Throws <see cref="CommandNotStartedException"/> when the command cannot be started at all.
    /// </summary>
    Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken);
}
