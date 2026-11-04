namespace Coanda.Core.Registry;

public static class CommandRegistry
{
    private static readonly IReadOnlySet<Role> Everyone = new HashSet<Role> { Role.Owner, Role.Member, Role.Agent };
    private static readonly IReadOnlySet<Role> OwnerOnly = new HashSet<Role> { Role.Owner };
    private static readonly IReadOnlySet<Role> People = new HashSet<Role> { Role.Owner, Role.Member };

    public static readonly IReadOnlyList<RegistryEntry> Entries =
    [
        new("project.add", EntryKind.Command, "Register a project with its repository remote and primary branch.", OwnerOnly),
        new("project.set", EntryKind.Command, "Replace the checks a merge of a project's pull requests waits for.", OwnerOnly),
        new("project.set-profile", EntryKind.Command, "Name the credential profile of this node that a project's agent steps spawn with.", OwnerOnly),
        new("project.show", EntryKind.Query, "Show one registered project.", Everyone),
        new("project.list", EntryKind.Query, "List the projects registered on the node.", Everyone),
        new("parcel.list", EntryKind.Query, "List the parcels of a project.", Everyone),
        new("parcel.show", EntryKind.Query, "Show one parcel.", Everyone),
        new("parcel.add", EntryKind.Command, "Create a parcel of a type at an entry stage.", People),
        new("parcel.move", EntryKind.Command, "Move a parcel to a stage its type allows.", Everyone),
        new("parcel.set-field", EntryKind.Command, "Record a value in one of a parcel's fields, replacing the value it held.", Everyone),
        new("parcel.close", EntryKind.Command, "Close a parcel with a resolution and a reason.", People),
        new("type.list", EntryKind.Query, "List the installed parcel types.", Everyone),
        new("flow.list", EntryKind.Query, "List the flows that govern a project.", Everyone),
        new("agent.list", EntryKind.Query, "List the agents that resolution returns for a project.", Everyone),
        new("flow-run.list", EntryKind.Query, "List the flow runs of a project or of one parcel.", Everyone),
        new("flow-run.show", EntryKind.Query, "Show one flow run with its steps.", Everyone),
        new("flow-run.start", EntryKind.Command, "Start a flow run of a flow on a parcel.", Everyone),
        new("flow-run.step", EntryKind.Command, "Record a flow run entering a step.", Everyone),
        new("flow-run.respawn", EntryKind.Command, "Record a resumed agent step spawning its agent again on the same visit.", Everyone),
        new("flow-run.spawn", EntryKind.Command, "Record an agent step launching its agent on the spawn its visit counted.", Everyone),
        new("flow-run.session", EntryKind.Command, "Record the session an executor returned for an agent step's launch.", Everyone),
        new("flow-run.answer", EntryKind.Command, "Record an agent step's answer and its measurement.", Everyone),
        new("flow-run.usage", EntryKind.Command, "Record what an agent step spent when its answer or its pause was not recorded.", Everyone),
        new("flow-run.result", EntryKind.Command, "Record a system step's result.", Everyone),
        new("flow-run.pause", EntryKind.Command, "Pause a flow run with a reason and a lever.", Everyone),
        new("flow-run.resume", EntryKind.Command, "Resume a paused flow run.", People),
        new("flow-run.end", EntryKind.Command, "End a flow run with its flow's outcome.", Everyone),
        new("flow-run.stop", EntryKind.Command, "Stop a live flow run before its end.", People),
        new("event.record", EntryKind.Command, "Record an external event in a project: a kind and named string fields.", People),
        new("subscription.add", EntryKind.Command, "Subscribe, in a project, to external events that create a parcel and start a flow.", People),
        new("subscription.list", EntryKind.Query, "List the subscriptions of a project.", Everyone),
        new("definition.draft", EntryKind.Command, "Record a draft of a parcel type, flow, or agent; a draft governs nothing.", Everyone),
        new("definition.publish", EntryKind.Command, "Publish a draft so that it governs work.", OwnerOnly),
        new("definition.retire", EntryKind.Command, "Retire a published definition so that it no longer governs work.", OwnerOnly),
        new("daemon.status", EntryKind.Query, "Report the daemon's state.", Everyone),
        new("daemon.stop", EntryKind.Command, "Stop the daemon.", People),
    ];

    public static RegistryEntry Require(string name, Principal principal)
    {
        RegistryEntry? entry = Entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
        return entry switch
        {
            null => throw new DomainException($"'{name}' is not in the registry."),
            _ when !entry.Allows(principal) => throw new DomainException($"'{name}' is not open to the {principal.Role} role."),
            _ => entry,
        };
    }
}
