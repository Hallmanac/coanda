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
/// <remarks>
/// A home that exists and holds no record is first given a copy of the <see cref="StoreTemplate"/>, a record
/// with the store's schema and no row, so the start does not create every table over again. A test whose
/// subject is what a start does to a home with no record starts through <see cref="StartOnNewHomeAsync(NodeHome, CancellationToken)"/>,
/// which starts over the home as it is.
/// </remarks>
public static class Daemons
{
    public static Task<RunningDaemon> StartAsync(NodeHome home, CancellationToken cancellationToken) =>
        StartAsync(home, new DaemonOptions(), cancellationToken);

    public static Task<RunningDaemon> StartAsync(NodeHome home, DaemonOptions options, CancellationToken cancellationToken) =>
        StartAsync(home, options, new SoftwareHosting(), cancellationToken);

    internal static Task<RunningDaemon> StartAsync(NodeHome home, SoftwareHosting software, CancellationToken cancellationToken) =>
        StartAsync(home, new DaemonOptions(), software, cancellationToken);

    internal static async Task<RunningDaemon> StartAsync(NodeHome home, DaemonOptions options, SoftwareHosting software, CancellationToken cancellationToken)
    {
        await StoreTemplate.CopyIntoAsync(home, cancellationToken);
        return await StartOnNewHomeAsync(home, options, software, cancellationToken);
    }

    /// <summary>Starts over <paramref name="home"/> as it is, giving a home with no record no copy of the <see cref="StoreTemplate"/>.</summary>
    public static Task<RunningDaemon> StartOnNewHomeAsync(NodeHome home, CancellationToken cancellationToken) =>
        StartOnNewHomeAsync(home, new DaemonOptions(), new SoftwareHosting(), cancellationToken);

    private static Task<RunningDaemon> StartOnNewHomeAsync(NodeHome home, DaemonOptions options, SoftwareHosting software, CancellationToken cancellationToken) =>
        DaemonHost.StartAsync(
            home,
            0,
            ModuleComposition.Compose(
                options with { CommandRunner = options.CommandRunner ?? new ScriptedCommandRunner() },
                software with { GitHubHandler = software.GitHubHandler ?? new FakeGitHub().Handler }),
            cancellationToken);
}
