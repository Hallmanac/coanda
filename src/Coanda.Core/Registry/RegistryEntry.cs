namespace Coanda.Core.Registry;

public enum EntryKind
{
    Query,
    Command,
    Action,
}

/// <summary>
/// One entry in the registry: a query reads the record, a command changes it, an action does
/// something in the world. Every surface (CLI, window, portal, MCP) is a rendering of this table,
/// filtered by the caller's role.
/// </summary>
public sealed record RegistryEntry(string Name, EntryKind Kind, string Description, IReadOnlySet<Role> AllowedRoles)
{
    public bool Allows(Principal principal) => AllowedRoles.Contains(principal.Role);
}
