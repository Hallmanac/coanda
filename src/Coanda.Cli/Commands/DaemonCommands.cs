using System.ComponentModel;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Modules.Software.GitHub;
using Coanda.Sdk.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

public sealed class DaemonRunSettings : CommandSettings
{
    [CommandOption("--port <PORT>")]
    [Description("Loopback port to bind; 0 picks a free one.")]
    public int Port { get; init; }

    [CommandOption("--log-to-file")]
    [Description("Write the daemon's log to the node home instead of the console (what a detached start uses).")]
    public bool LogToFile { get; init; }
}

/// <summary>Runs the daemon in the foreground until stopped. `coa daemon start` launches this detached.</summary>
public sealed class DaemonRunCommand : AsyncCommand<DaemonRunSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DaemonRunSettings settings, CancellationToken cancellationToken)
    {
        NodeHome home = NodeHome.Resolve();
        if (settings.LogToFile)
        {
            StreamWriter log = OpenLog(home);
            Console.SetOut(log);
            Console.SetError(log);

            // The exception handler prints a refusal through AnsiConsole, which keeps the console
            // it was created on, so it is pointed at the log too or a refusal would miss it. A log
            // line is not wrapped at a terminal's width.
            IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(log),
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
            });
            console.Profile.Width = 4096;
            AnsiConsole.Console = console;
        }

        // Read after the redirect, so a refusal lands in the log, and before anything binds a port.
        // The daemon a person runs starts real processes for its worktree, gates, and open-pr steps,
        // and its open-pr step reaches GitHub over the network.
        DaemonOptions options = NodeConfiguration.Load(home) with
        {
            CommandRunner = new ProcessCommandRunner(),
            GitHubHandler = GitHubClient.NetworkHandler,
        };
        await using RunningDaemon daemon = await DaemonHost.StartAsync(home, settings.Port, options, cancellationToken);
        Console.WriteLine($"coanda daemon {daemon.Info.Version} listening on {daemon.Info.BaseAddress} (pid {daemon.Info.Pid}), home {home.Path}");
        await daemon.WaitForShutdownAsync(cancellationToken);
        return 0;
    }

    /// <summary>
    /// Creates or narrows the home, then opens its log for appending. The log is owner-only from
    /// the moment it is created, and a log an earlier run left wider is narrowed before this writes to it.
    /// </summary>
    internal static StreamWriter OpenLog(NodeHome home)
    {
        home.EnsureExists();
        return new StreamWriter(OwnerOnly.Open(home.LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }
}

public sealed class DaemonStartCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        DaemonInfo? already = await client.FindRunningAsync(cancellationToken);
        if (already is not null)
        {
            if (DaemonClient.RefusalToStartBeside(already) is { } refusal)
            {
                throw new DomainException(refusal);
            }

            AnsiConsole.MarkupLineInterpolated($"The daemon is already running on {already.BaseAddress} (pid {already.Pid}).");
            return 0;
        }

        DaemonInfo info = await client.EnsureRunningAsync(cancellationToken);
        AnsiConsole.MarkupLineInterpolated($"Started the daemon on {info.BaseAddress} (pid {info.Pid}); log at {client.Home.LogPath}.");
        return 0;
    }
}

public sealed class DaemonStopCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        await client.StopAsync(cancellationToken);
        AnsiConsole.MarkupLine("Asked the daemon to stop.");
        return 0;
    }
}

public sealed class DaemonStatusCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        DaemonStatusView? status = await client.StatusAsync(cancellationToken);
        if (status is null)
        {
            AnsiConsole.MarkupLineInterpolated($"No daemon is running. Home: {client.Home.Path}");
            return 0;
        }

        var table = new Table().HideHeaders().AddColumns("k", "v");
        foreach ((string key, string value) in StatusRows(status))
        {
            table.AddRow(key, value);
        }

        AnsiConsole.Write(table);
        return 0;
    }

    /// <summary>
    /// The rows <c>coa daemon status</c> prints, as markup with the text the daemon sends escaped: its
    /// version, its home, and its database. The address, pid, and start time are built from numbers
    /// and a date and need no escaping.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> StatusRows(DaemonStatusView status) =>
    [
        ("Version", Markup.Escape(string.IsNullOrWhiteSpace(status.Version) ? "(none recorded)" : status.Version)),
        ("Address", $"http://127.0.0.1:{status.Port}"),
        ("Pid", status.Pid.ToString()),
        ("Started", status.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz")),
        ("Home", Markup.Escape(status.Home)),
        ("Database", Markup.Escape(status.Database)),
    ];
}
