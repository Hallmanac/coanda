namespace Coanda.Core.Registry;

public static class CommandRegistry
{
    private static readonly IReadOnlySet<Role> Everyone = new HashSet<Role> { Role.Owner, Role.Member, Role.Agent };
    private static readonly IReadOnlySet<Role> OwnerOnly = new HashSet<Role> { Role.Owner };
    private static readonly IReadOnlySet<Role> People = new HashSet<Role> { Role.Owner, Role.Member };

    public static readonly IReadOnlyList<RegistryEntry> Entries =
    [
        new("project.add", EntryKind.Command, "Register a project with its repository remote and primary branch.", OwnerOnly),
        new("project.show", EntryKind.Query, "Show one registered project.", Everyone),
        new("project.list", EntryKind.Query, "List the projects registered on the node.", Everyone),
        new("parcel.list", EntryKind.Query, "List the parcels of a project.", Everyone),
        new("parcel.show", EntryKind.Query, "Show one parcel.", Everyone),
        new("parcel.add", EntryKind.Command, "Create a parcel of a type at an entry state.", People),
        new("parcel.move", EntryKind.Command, "Move a parcel to a state its type allows.", Everyone),
        new("parcel.set-field", EntryKind.Command, "Record a value in one of a parcel's fields, replacing the value it held.", Everyone),
        new("parcel.close", EntryKind.Command, "Close a parcel with a resolution and a reason.", People),
        new("type.list", EntryKind.Query, "List the installed parcel types.", Everyone),
        new("contour.list", EntryKind.Query, "List the contours that govern a project.", Everyone),
        new("agent.list", EntryKind.Query, "List the agents that resolution returns for a project.", Everyone),
        new("flow.list", EntryKind.Query, "List the flows of a project or of one parcel.", Everyone),
        new("flow.show", EntryKind.Query, "Show one flow with its steps.", Everyone),
        new("flow.start", EntryKind.Command, "Start a flow of a contour on a parcel.", Everyone),
        new("flow.step", EntryKind.Command, "Record a flow entering a step.", Everyone),
        new("flow.respawn", EntryKind.Command, "Record a resumed judgment spawning its agent again on the same visit.", Everyone),
        new("flow.spawn", EntryKind.Command, "Record a judgment launching its agent on the spawn its visit counted.", Everyone),
        new("flow.session", EntryKind.Command, "Record the session an executor returned for a judgment's launch.", Everyone),
        new("flow.answer", EntryKind.Command, "Record a judgment's answer and its measurement.", Everyone),
        new("flow.usage", EntryKind.Command, "Record what a judgment spent when its answer or its pause was not recorded.", Everyone),
        new("flow.result", EntryKind.Command, "Record an action's result.", Everyone),
        new("flow.pause", EntryKind.Command, "Pause a flow with a reason and a lever.", Everyone),
        new("flow.resume", EntryKind.Command, "Resume a paused flow.", People),
        new("flow.end", EntryKind.Command, "End a flow with its contour's outcome.", Everyone),
        new("flow.stop", EntryKind.Command, "Stop a live flow before its end.", People),
        new("event.record", EntryKind.Command, "Record an external event in a project: a kind and named string fields.", People),
        new("subscription.add", EntryKind.Command, "Subscribe, in a project, to external events that create a parcel and start a contour.", People),
        new("subscription.list", EntryKind.Query, "List the subscriptions of a project.", Everyone),
        new("definition.draft", EntryKind.Command, "Record a draft of a parcel type, contour, or agent; a draft governs nothing.", Everyone),
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
