using Coanda.Core;

namespace Coanda.Daemon.Store;

/// <summary>
/// The tenant ids the store uses. A project is a tenant named by the project. Owner-scope and
/// shipped-scope definitions are node-level records and live in one reserved tenant that no
/// project name can reach, because every project command refuses it here.
/// </summary>
public static class Tenants
{
    /// <summary>The tenant holding owner-scope and shipped-scope definitions.</summary>
    public const string Node = "_node";

    /// <summary>Returns the project name trimmed, or throws when it is blank or names the node tenant.</summary>
    public static string RequireProject(string project)
    {
        string trimmed = project.Trim();
        return trimmed switch
        {
            "" => throw new DomainException("A project name is required; none was given."),
            _ when string.Equals(trimmed, Node, StringComparison.OrdinalIgnoreCase) =>
                throw new DomainException($"'{project}' is reserved for node-level records and cannot name a project."),
            _ => trimmed,
        };
    }
}
