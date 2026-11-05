using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;

namespace Coanda.Tests.Core.Identity;

/// <summary>
/// Builds a fixture of identity records, each signed by a party from its own node with the next
/// sequence in that node's chain, unless a test names another origin.
/// </summary>
public sealed class Ledger
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly List<IdentityRecord> records = [];
    private readonly Dictionary<Guid, long> sequences = [];

    public IReadOnlyList<IdentityRecord> Records => records;

    public Ledger Sign(Party signer, IIdentityEvent identityEvent, Guid? origin = null)
    {
        Guid from = origin ?? signer.Node;
        long sequence = sequences.GetValueOrDefault(from) + 1;
        sequences[from] = sequence;
        records.Add(new IdentityRecord(identityEvent, signer.Key, from, sequence));
        return this;
    }

    public Ledger Root(Party root) => Sign(root, new OwnerRootRecorded(root.Node, root.Key, At));

    public Ledger Vouch(Party by, Party vouched) => Sign(by, new NodeVouched(vouched.Node, vouched.Key, At));

    public Ledger Vouch(Party by, Guid node, string key) => Sign(by, new NodeVouched(node, key, At));

    public Ledger Revoke(Party by, Party revoked) => Sign(by, new NodeRevoked(revoked.Node, revoked.Key, At));

    public Ledger Revoke(Party by, Guid node, string key) => Sign(by, new NodeRevoked(node, key, At));

    public Ledger Name(Party by, Party successor) => Sign(by, new SuccessorNamed(successor.Node, successor.Key, At));

    public Ledger Rotate(Party successor, Party superseded) => Sign(successor, new RootRotated(successor.Key, superseded.Key, At));

    public Ledger RevokeSuccessor(Party by, Party successor) => Sign(by, new SuccessorRevoked(successor.Key, At));

    public Ledger Add(Party by, Party member, MemberRole role = MemberRole.Member) =>
        Sign(by, new MemberAdded(member.Node, member.Key, role, At));

    public Ledger Remove(Party by, Party member) => Sign(by, new MemberRemoved(member.Key, At));
}
