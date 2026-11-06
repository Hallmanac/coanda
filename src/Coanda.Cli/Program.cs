using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Daemon.Executors.ClaudeCode;
using Spectre.Console;
using Spectre.Console.Cli;

if (args is [ToolGuardRules.Command, ..])
{
    // The pre-tool-use hook of an agent step's session. It is answered before the command app is
    // built, so neither Spectre's parse errors nor its exception handler decide the exit code, and
    // it starts no daemon.
    return ToolGuardHost.Run(args);
}

if (args is [SettingsEvidence.Command, ..])
{
    // The session-start hook that shows a session's CLI applied its settings document. It prints
    // only on standard error, which the CLI quotes on its output stream and keeps out of the
    // session's context, and it is answered before the command app is built for the same reasons.
    return SettingsEvidence.Run(args, Console.Error);
}

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

    config.AddBranch("record", record =>
    {
        record.SetDescription("The node's record: export it in this build's current shapes, and rebuild it from such an export. Both refuse while a daemon runs on the home.");
        record.AddCommand<RecordExportCommand>("export").WithDescription("Write a full export of the record to an owner-only file under the node home.").WithExample("record", "export");
        record.AddCommand<RecordRebuildCommand>("rebuild").WithDescription("Rebuild the record from an export, moving the existing database aside.")
            .WithExample("record", "rebuild", "~/.coanda/exports/record-20261105T120000000Z.json");
    });

    config.AddBranch("node", node =>
    {
        node.SetDescription("This node: its id and key, and its vouches for the other nodes of a project.");
        node.AddCommand<NodeShowCommand>("show").WithDescription("Show this node's id, its public key, and the key's fingerprint.").WithExample("node", "show");
        node.AddCommand<NodeVouchCommand>("vouch").WithDescription("Vouch in a project for a node id and its exact key, then publish the project.")
            .WithExample("node", "vouch", "0192a3b4-0000-7000-8000-000000000000", "<public-key>", "--project", "coanda");
    });

    config.AddCommand<JoinCommand>("join").WithDescription("Join a project from its remote and the fingerprint of its owner's root key, and import it.")
        .WithExample("join", "git@github.com:you/coanda.git", "--root", "sha256:<64 hex digits>");

    config.AddBranch("project", project =>
    {
        project.SetDescription("A project is a name and the repos its work acts on, each a git remote; a module's settings for it are set with coa project module.");
        project.AddCommand<ProjectAddCommand>("add").WithDescription("Register a project with its first repo's remote and the software module's primary branch, build, and test settings.")
            .WithExample("project", "add", "coanda", "--repo", "git@github.com:you/coanda.git", "--branch", "main");
        project.AddCommand<ProjectSetProfileCommand>("set-profile").WithDescription("Name the credential profile this node spawns the project's agent steps with.")
            .WithExample("project", "set-profile", "coanda", "work");
        project.AddCommand<ProjectShowCommand>("show").WithDescription("Show one registered project.").WithExample("project", "show", "coanda");
        project.AddCommand<ProjectListCommand>("list").WithDescription("List the projects registered on this node.").WithExample("project", "list");
        project.AddBranch("module", module =>
        {
            module.SetDescription("A module's settings in a project: replicated settings the project agrees on, and the credential profile this node selects.");
            module.AddCommand<ProjectModuleSetCommand>("set").WithDescription("Give one of a module's replicated settings in a project a value.")
                .WithExample("project", "module", "set", "coanda", "software", "required-checks", "'[\"build-test (ubuntu-latest)\"]'");
            module.AddCommand<ProjectModuleSelectProfileCommand>("select-profile").WithDescription("Select one of this node's credential profiles for a module in a project.")
                .WithExample("project", "module", "select-profile", "coanda", "software", "work");
            module.AddCommand<ProjectModuleShowCommand>("show").WithDescription("Show a module's settings in a project and whether it is configured on this node.")
                .WithExample("project", "module", "show", "coanda", "software");
        });
    });

    config.AddBranch("parcel", parcel =>
    {
        parcel.SetDescription("A parcel is the unit of work: one type, one lifecycle stage, moved by flow runs.");
        parcel.AddCommand<ParcelAddCommand>("add").WithDescription("Create a parcel at an entry stage of its type.").WithExample("parcel", "add", "--title", "\"Try Coanda\"");
        parcel.AddCommand<ParcelListCommand>("list").WithDescription("List the project's open parcels.").WithExample("parcel", "list");
        parcel.AddCommand<ParcelShowCommand>("show").WithDescription("Show one parcel.").WithExample("parcel", "show", "0192a3b4");
        parcel.AddCommand<ParcelMoveCommand>("move").WithDescription("Move a parcel to a stage its type allows.").WithExample("parcel", "move", "0192a3b4", "build");
        parcel.AddCommand<ParcelSetCommand>("set").WithDescription("Set one of a parcel's fields, replacing its value.")
            .WithExample("parcel", "set", "0192a3b4", "objective", "--text", "\"Teach the daemon fields\"")
            .WithExample("parcel", "set", "0192a3b4", "criteria", "--item", "\"It builds\"", "--item", "\"It tests\"")
            .WithExample("parcel", "set", "0192a3b4", "pull-request", "--ref", "42", "--url", "https://github.com/o/r/pull/42");
        parcel.AddCommand<ParcelClaimCommand>("claim").WithDescription("Claim a parcel for this node, publish its ledger, and fetch its peers'.").WithExample("parcel", "claim", "0192a3b4");
        parcel.AddCommand<ParcelCloseCommand>("close").WithDescription("Close a parcel with a resolution and a reason.").WithExample("parcel", "close", "0192a3b4", "-r", "completed", "--reason", "\"Merged\"");
    });

    config.AddBranch("agent", agent =>
    {
        agent.SetDescription("Agents are the personas that do the judging; the software module brings five.");
        agent.AddCommand<AgentListCommand>("list").WithDescription("List the agents that govern a project.").WithExample("agent", "list");
    });

    config.AddBranch("type", type =>
    {
        type.SetDescription("Parcel types define a lifecycle; the software module brings idea and task.");
        type.AddCommand<TypeListCommand>("list").WithDescription("List the installed parcel types.").WithExample("type", "list");
    });

    config.AddBranch("flow", flow =>
    {
        flow.SetDescription("Flows are the loops a flow run follows to move a parcel; the software module brings build, merge, discovery, and review-on-assignment.");
        flow.AddCommand<FlowListCommand>("list").WithDescription("List the flows that govern the project.").WithExample("flow", "list");
        flow.AddCommand<FlowStartCommand>("start").WithDescription("Start a flow run of a flow on a parcel; the daemon advances it from there.").WithExample("flow", "start", "build", "--parcel", "0192a3b4");
    });

    config.AddBranch("flow-run", flowRun =>
    {
        flowRun.SetDescription("A flow run is one execution of a flow on a parcel; a parcel has at most one live flow run.");
        flowRun.AddCommand<FlowRunListCommand>("list").WithDescription("List a parcel's flow runs, oldest first.").WithExample("flow-run", "list", "--parcel", "0192a3b4");
        flowRun.AddCommand<FlowRunShowCommand>("show").WithDescription("Show one flow run and the steps it entered.").WithExample("flow-run", "show", "0192a3b4");
        flowRun.AddCommand<FlowRunPauseCommand>("pause").WithDescription("Pause a running flow run; a resume performs its current step again.").WithExample("flow-run", "pause", "0192a3b4", "--reason", "\"Waiting on the API owner\"");
        flowRun.AddCommand<FlowRunStopCommand>("stop").WithDescription("Stop a live flow run, releasing its parcel.").WithExample("flow-run", "stop", "0192a3b4", "--reason", "\"Taking this by hand\"");
        flowRun.AddCommand<FlowRunResumeCommand>("resume").WithDescription("Resume a paused flow run; the daemon advances it from there.").WithExample("flow-run", "resume", "0192a3b4");
    });
});

return await app.RunAsync(args);
