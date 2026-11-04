using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Tests.Daemon.Commands;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The one way a test starts the daemon host: on a free port, with a scripted command runner
/// unless the options already carry one, so no test starts a process through the worktree or gates
/// actions or a worktree's removal.
/// </summary>
public static class Daemons
{
    public static Task<RunningDaemon> StartAsync(NodeHome home, CancellationToken cancellationToken) =>
        StartAsync(home, new DaemonOptions(), cancellationToken);

    public static Task<RunningDaemon> StartAsync(NodeHome home, DaemonOptions options, CancellationToken cancellationToken) =>
        DaemonHost.StartAsync(
            home,
            0,
            options.CommandRunner is null ? options with { CommandRunner = new ScriptedCommandRunner() } : options,
            cancellationToken);
}
