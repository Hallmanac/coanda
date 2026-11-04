using System.ComponentModel;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
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
        DaemonOptions options = NodeConfiguration.Load(home);
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
        DaemonInfo? info = await client.FindRunningAsync(cancellationToken);
        if (info is null)
        {
            AnsiConsole.MarkupLineInterpolated($"No daemon is running. Home: {client.Home.Path}");
            return 0;
        }

        DaemonStatusView status = await client.GetAsync<DaemonStatusView>("/status", cancellationToken);
        var table = new Table().HideHeaders().AddColumns("k", "v");
        table.AddRow("Version", status.Version);
        table.AddRow("Address", info.BaseAddress);
        table.AddRow("Pid", status.Pid.ToString());
        table.AddRow("Started", status.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz"));
        table.AddRow("Home", status.Home);
        table.AddRow("Database", status.Database);
        AnsiConsole.Write(table);
        return 0;
    }
}
