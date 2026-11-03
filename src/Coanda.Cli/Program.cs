using Coanda.Cli.Commands;
using Coanda.Core;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("coa");
    config.SetApplicationVersion(Coanda.Daemon.Api.DaemonHost.Version);
    config.SetExceptionHandler((exception, _) =>
    {
        if (exception is DomainException domain)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{domain.Message}[/]");
            return 2;
        }

        AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
        return 1;
    });

    config.AddBranch("daemon", daemon =>
    {
        daemon.SetDescription("The daemon is the only writer to this node's record; every command goes through it.");
        daemon.AddCommand<DaemonRunCommand>("run").WithDescription("Run the daemon in the foreground.").WithExample("daemon", "run");
        daemon.AddCommand<DaemonStartCommand>("start").WithDescription("Start the daemon detached (also happens on demand).").WithExample("daemon", "start");
        daemon.AddCommand<DaemonStopCommand>("stop").WithDescription("Stop the daemon.").WithExample("daemon", "stop");
        daemon.AddCommand<DaemonStatusCommand>("status").WithDescription("Report the daemon's address, pid, and home.").WithExample("daemon", "status");
    });

    config.AddBranch("parcel", parcel =>
    {
        parcel.SetDescription("A parcel is the unit of work: one type, one lifecycle state, moved by flows.");
        parcel.AddCommand<ParcelAddCommand>("add").WithDescription("Create a parcel at an entry state of its type.").WithExample("parcel", "add", "--title", "\"Try Coanda\"");
        parcel.AddCommand<ParcelListCommand>("list").WithDescription("List the project's open parcels.").WithExample("parcel", "list");
        parcel.AddCommand<ParcelShowCommand>("show").WithDescription("Show one parcel.").WithExample("parcel", "show", "0192a3b4");
        parcel.AddCommand<ParcelMoveCommand>("move").WithDescription("Move a parcel to a state its type allows.").WithExample("parcel", "move", "0192a3b4", "build");
        parcel.AddCommand<ParcelCloseCommand>("close").WithDescription("Close a parcel with a resolution and a reason.").WithExample("parcel", "close", "0192a3b4", "-r", "completed", "--reason", "\"Merged\"");
    });

    config.AddBranch("agent", agent =>
    {
        agent.SetDescription("Agents are the personas that do the judging; five ship installed.");
        agent.AddCommand<AgentListCommand>("list").WithDescription("List the agents that govern a project.").WithExample("agent", "list");
    });

    config.AddBranch("type", type =>
    {
        type.SetDescription("Parcel types define a lifecycle; idea and task ship installed.");
        type.AddCommand<TypeListCommand>("list").WithDescription("List the installed parcel types.").WithExample("type", "list");
    });

    config.AddBranch("flow", flow =>
    {
        flow.SetDescription("Flows are the loops that move parcels; build, discovery, and review-on-assignment ship installed.");
        flow.AddCommand<FlowListCommand>("list").WithDescription("List the flows that govern the project.").WithExample("flow", "list");
    });
});

return await app.RunAsync(args);
