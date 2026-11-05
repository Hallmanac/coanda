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
    public static NodeOwner OfAccount() => new(Environment.UserName);
}
