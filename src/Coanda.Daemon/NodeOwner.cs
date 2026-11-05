using Coanda.Core.Registry;

namespace Coanda.Daemon;

/// <summary>
/// The node's owner: the operating system account the daemon runs as, whose principal id is that
/// account's name, the value the CLI sends in its principal header. A call holding only the daemon
/// token acts as this principal. This is the one place the owner's id is read.
/// </summary>
public sealed class NodeOwner(string id)
{
    /// <summary>The owner's principal id.</summary>
    public string Id { get; } = string.IsNullOrWhiteSpace(id)
        ? throw new ArgumentException("The node's owner needs an id.", nameof(id))
        : id;

    /// <summary>The owner's principal, with the Owner role.</summary>
    public Principal Principal => new(Id, Role.Owner);

    /// <summary>The owner this daemon runs as: the operating system account's name.</summary>
    public static NodeOwner OfAccount() => OfAccount(Environment.UserName);

    /// <summary>
    /// The owner for the account named <paramref name="accountName"/>. The runtime names no account
    /// when the process runs as a user it cannot look up (a container's uid with no passwd entry, for
    /// one), and the daemon still starts then: its owner takes the id <see cref="Principal.LocalOwner"/>
    /// carries. The CLI in that same environment sends a blank principal header, which acts as the
    /// owner whatever its id.
    /// </summary>
    public static NodeOwner OfAccount(string? accountName) => string.IsNullOrWhiteSpace(accountName)
        ? new(Principal.LocalOwner.Id)
        : new(accountName);
}
