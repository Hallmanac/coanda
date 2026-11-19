using Coanda.Cli;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Modules.Software.GitHub;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The one way a test starts the daemon host: on a free port, composed as <c>coa daemon run</c> composes
/// it (<see cref="ModuleComposition"/>), with a scripted command runner unless the options already carry
/// one, so no test starts a process through the worktree, gates, or open-pr system steps or a worktree's
/// removal, and with a <see cref="FakeGitHub"/> unless the software module's hosting already carries a
/// GitHub handler, so no test reaches GitHub.
/// </summary>
public static class Daemons
{
    public static Task<RunningDaemon> StartAsync(NodeHome home, CancellationToken cancellationToken) =>
        StartAsync(home, new DaemonOptions(), cancellationToken);

    public static Task<RunningDaemon> StartAsync(NodeHome home, DaemonOptions options, CancellationToken cancellationToken) =>
        StartAsync(home, options, new SoftwareHosting(), cancellationToken);

    internal static Task<RunningDaemon> StartAsync(NodeHome home, SoftwareHosting software, CancellationToken cancellationToken) =>
        StartAsync(home, new DaemonOptions(), software, cancellationToken);

    internal static Task<RunningDaemon> StartAsync(NodeHome home, DaemonOptions options, SoftwareHosting software, CancellationToken cancellationToken) =>
        DaemonHost.StartAsync(
            home,
            0,
            ModuleComposition.Compose(
                options with { CommandRunner = options.CommandRunner ?? new ScriptedCommandRunner() },
                software with { GitHubHandler = software.GitHubHandler ?? new FakeGitHub().Handler }),
            cancellationToken);
}
