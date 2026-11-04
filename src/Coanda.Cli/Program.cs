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

    config.AddBranch("project", project =>
    {
        project.SetDescription("A project is a record of the one repository its work acts on: a remote and a primary branch.");
        project.AddCommand<ProjectAddCommand>("add").WithDescription("Register a project with its repository's remote and primary branch.")
            .WithExample("project", "add", "coanda", "--repo", "git@github.com:you/coanda.git", "--branch", "main");
        project.AddCommand<ProjectShowCommand>("show").WithDescription("Show one registered project.").WithExample("project", "show", "coanda");
        project.AddCommand<ProjectListCommand>("list").WithDescription("List the projects registered on this node.").WithExample("project", "list");
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

    config.AddBranch("contour", contour =>
    {
        contour.SetDescription("Contours are the loops a flow follows to move a parcel; build, discovery, and review-on-assignment ship installed.");
        contour.AddCommand<ContourListCommand>("list").WithDescription("List the contours that govern the project.").WithExample("contour", "list");
        contour.AddCommand<ContourStartCommand>("start").WithDescription("Start a flow of a contour on a parcel; the daemon advances it from there.").WithExample("contour", "start", "build", "--parcel", "0192a3b4");
    });

    config.AddBranch("flow", flow =>
    {
        flow.SetDescription("A flow is one execution of a contour on a parcel; a parcel has at most one live flow.");
        flow.AddCommand<FlowListCommand>("list").WithDescription("List a parcel's flows, oldest first.").WithExample("flow", "list", "--parcel", "0192a3b4");
        flow.AddCommand<FlowShowCommand>("show").WithDescription("Show one flow and the steps it entered.").WithExample("flow", "show", "0192a3b4");
        flow.AddCommand<FlowPauseCommand>("pause").WithDescription("Pause a running flow; a resume performs its current step again.").WithExample("flow", "pause", "0192a3b4", "--reason", "\"Waiting on the API owner\"");
        flow.AddCommand<FlowStopCommand>("stop").WithDescription("Stop a live flow, releasing its parcel.").WithExample("flow", "stop", "0192a3b4", "--reason", "\"Taking this by hand\"");
        flow.AddCommand<FlowResumeCommand>("resume").WithDescription("Resume a paused flow; the daemon advances it from there.").WithExample("flow", "resume", "0192a3b4");
    });
});

return await app.RunAsync(args);
