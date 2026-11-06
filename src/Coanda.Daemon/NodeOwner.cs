using Coanda.Core;
using Coanda.Core.Registry;

namespace Coanda.Daemon;

/// <summary>
/// The node's owner: one principal id per node, the value the CLI sends in its principal header. A call
/// holding only the daemon token acts as this principal. The id is the node-local owner id record
/// (<see cref="NodeOwnerRecord"/>) when the home holds one: the id that registered a project here, or, on
/// a node that joined a project, the id that registered the owner's registration it imported. A node with
/// no record keeps the operating system account's name until it registers or joins. This is the one place
/// the daemon reads the owner's id.
/// </summary>
public sealed class NodeOwner
{
    private readonly NodeHome? home;
    private readonly Lock gate = new();
    private string id;
    private bool recorded;

    /// <summary>An owner with the id <paramref name="id"/> and no home to record it in, as a test or a store opened without the daemon has.</summary>
    public NodeOwner(string id)
        : this(id, null, recorded: false)
    {
    }

    private NodeOwner(string id, NodeHome? home, bool recorded)
    {
        this.id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("The node's owner needs an id.", nameof(id))
            : id;
        this.home = home;
        this.recorded = recorded;
    }

    /// <summary>The owner's principal id.</summary>
    public string Id
    {
        get
        {
            lock (gate)
            {
                return id;
            }
        }
    }

    /// <summary>Whether the id is the home's owner id record rather than the account's name.</summary>
    public bool Recorded
    {
        get
        {
            lock (gate)
            {
                return recorded;
            }
        }
    }

    /// <summary>The owner's principal, with the Owner role.</summary>
    public Principal Principal => new(Id, Role.Owner);

    /// <summary>The owner of <paramref name="home"/>: its owner id record, or the operating system account's name when it holds none.</summary>
    public static NodeOwner Of(NodeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return NodeOwnerRecord.Read(home) is { } recordedId
            ? new NodeOwner(recordedId, home, recorded: true)
            : new NodeOwner(OfAccount().Id, home, recorded: false);
    }

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

    /// <summary>
    /// Why this node may not take <paramref name="ownerId"/> as its owner id, or null when it may: a node keeps
    /// one owner id, so one already recorded and different refuses it.
    /// </summary>
    public string? RefusalOf(string ownerId)
    {
        lock (gate)
        {
            return Refused(ownerId);
        }
    }

    private string? Refused(string ownerId) =>
        recorded && !string.Equals(id, ownerId, StringComparison.Ordinal)
            ? $"This node already records the owner id '{id}', not '{ownerId}'; a node keeps one owner id."
            : null;

    /// <summary>
    /// Records <paramref name="ownerId"/> as this node's owner id, in the home's record, and acts as it from
    /// now on. Recording the id already recorded changes nothing; another id is refused (<see cref="RefusalOf"/>).
    /// </summary>
    public void Record(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            throw new DomainException("The node's owner id cannot be blank.");
        }

        lock (gate)
        {
            if (Refused(ownerId) is { } refused)
            {
                throw new DomainException(refused);
            }

            if (recorded)
            {
                return;
            }

            if (home is not null)
            {
                NodeOwnerRecord.Write(home, ownerId);
            }

            id = ownerId;
            recorded = true;
        }
    }
}
