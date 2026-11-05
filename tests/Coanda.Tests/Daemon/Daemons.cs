using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Modules.Software.GitHub;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The one way a test starts the daemon host: on a free port, with a scripted command runner
/// unless the options already carry one, so no test starts a process through the worktree, gates,
/// or open-pr system steps or a worktree's removal, and with a <see cref="FakeGitHub"/> unless the options
/// already carry a GitHub handler, so no test reaches GitHub.
/// </summary>
public static class Daemons
{
    public static Task<RunningDaemon> StartAsync(NodeHome home, CancellationToken cancellationToken) =>
        StartAsync(home, new DaemonOptions(), cancellationToken);

    public static Task<RunningDaemon> StartAsync(NodeHome home, DaemonOptions options, CancellationToken cancellationToken) =>
        DaemonHost.StartAsync(
            home,
            0,
            options with
            {
                CommandRunner = options.CommandRunner ?? new ScriptedCommandRunner(),
                GitHubHandler = options.GitHubHandler ?? new FakeGitHub().Handler,
            },
            cancellationToken);
}
