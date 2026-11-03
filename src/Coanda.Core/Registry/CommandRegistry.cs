namespace Coanda.Core.Registry;

public static class CommandRegistry
{
    private static readonly IReadOnlySet<Role> Everyone = new HashSet<Role> { Role.Owner, Role.Member, Role.Agent };
    private static readonly IReadOnlySet<Role> People = new HashSet<Role> { Role.Owner, Role.Member };

    public static readonly IReadOnlyList<RegistryEntry> Entries =
    [
        new("parcel.list", EntryKind.Query, "List the parcels of a project.", Everyone),
        new("parcel.show", EntryKind.Query, "Show one parcel.", Everyone),
        new("parcel.add", EntryKind.Command, "Create a parcel of a type at an entry state.", People),
        new("parcel.move", EntryKind.Command, "Move a parcel to a state its type allows.", Everyone),
        new("parcel.close", EntryKind.Command, "Close a parcel with a resolution and a reason.", People),
        new("type.list", EntryKind.Query, "List the installed parcel types.", Everyone),
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
