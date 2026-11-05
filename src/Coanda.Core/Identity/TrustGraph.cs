using Coanda.Core.Identity.Events;

namespace Coanda.Core.Identity;

/// <summary>
/// Who may sign for which node, for one state of the acts: the pairings trust reaches, those in
/// conflict, and the pairings the vouches propose.
/// </summary>
internal sealed class TrustGraph
{
    private readonly HashSet<(Guid NodeId, string Key)> trusted;
    private readonly HashSet<(Guid NodeId, string Key)> anchors;
    private readonly HashSet<(Guid NodeId, string Key)> conflicted;
    private readonly HashSet<(Guid NodeId, string Key)> proposed;

    private TrustGraph(
        HashSet<(Guid NodeId, string Key)> trusted,
        HashSet<(Guid NodeId, string Key)> anchors,
        HashSet<(Guid NodeId, string Key)> conflicted,
        HashSet<(Guid NodeId, string Key)> proposed)
    {
        this.trusted = trusted;
        this.anchors = anchors;
        this.conflicted = conflicted;
        this.proposed = proposed;
    }

    public static TrustGraph Compute(IdentityRecord[] all, IReadOnlyList<RankedKey> chain, Acts acts)
    {
        // Pairings bound by a root-set key's rank or by a member add; a vouch may not pair either side with another.
        HashSet<(Guid NodeId, string Key)> bound =
        [
            .. chain.Select(k => (k.NodeId, k.Key)),
            .. acts.Adds.Select(a => (a.NodeId, a.Key)),
        ];

        HashSet<(Guid NodeId, string Key)> anchors =
        [
            .. chain.Select(k => (k.NodeId, k.Key)),
            .. acts.Adds.Where(a => !acts.MayNotSign(a.Key)).Select(a => (a.NodeId, a.Key)),
        ];

        (Guid Origin, string Signer, (Guid NodeId, string Key) Pairing)[] proposals =
        [
            .. IdentityTrust.Of<NodeVouched>(all)
                .Select(r => (r.Record.Origin, r.Record.Signer, Pairing: (r.Event.NodeId, r.Event.Key)))
                .Where(v => !bound.Any(b => (b.NodeId == v.Pairing.NodeId) != (b.Key == v.Pairing.Key))),
        ];

        HashSet<(Guid NodeId, string Key)> proposed = [.. proposals.Select(v => v.Pairing)];

        (Guid Origin, string Signer, (Guid NodeId, string Key) Pairing)[] vouches =
        [
            .. proposals.Where(v => !acts.MayNotSign(v.Signer) && !acts.MayNotSign(v.Pairing.Key)),
        ];

        HashSet<(Guid NodeId, string Key)> reached = Follow(anchors, vouches, []);
        HashSet<(Guid NodeId, string Key)> conflicted =
        [
            .. reached.Where(p => reached.Any(o => o != p && (o.NodeId == p.NodeId || o.Key == p.Key))),
        ];

        HashSet<(Guid NodeId, string Key)> trusted = Follow(anchors, vouches, conflicted);
        return new TrustGraph(trusted, anchors, conflicted, proposed);
    }

    /// <summary>Whether <paramref name="key"/> may sign for <paramref name="nodeId"/>.</summary>
    public bool Trusts(Guid nodeId, string key) => trusted.Contains((nodeId, key));

    /// <summary>Whether a counting vouch, not a rank or a member add, gives <paramref name="nodeId"/> exactly <paramref name="key"/>.</summary>
    public bool IsVouched(Guid nodeId, string key) => Trusts(nodeId, key) && !anchors.Contains((nodeId, key));

    /// <summary>The key a counting vouch gives <paramref name="nodeId"/>, if one does.</summary>
    public string? VouchedKeyOf(Guid nodeId) =>
        trusted.Where(p => p.NodeId == nodeId && !anchors.Contains(p)).Select(p => p.Key).FirstOrDefault();

    public bool IsConflicted(Guid nodeId, string key) => conflicted.Contains((nodeId, key));

    /// <summary>Whether some vouch pairs <paramref name="nodeId"/> with <paramref name="key"/> without breaking a binding.</summary>
    public bool IsProposed(Guid nodeId, string key) => proposed.Contains((nodeId, key));

    private static HashSet<(Guid NodeId, string Key)> Follow(
        HashSet<(Guid NodeId, string Key)> anchors,
        (Guid Origin, string Signer, (Guid NodeId, string Key) Pairing)[] vouches,
        HashSet<(Guid NodeId, string Key)> excluded)
    {
        HashSet<(Guid NodeId, string Key)> reached = [.. anchors.Where(a => !excluded.Contains(a))];
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((Guid origin, string signer, (Guid NodeId, string Key) pairing) in vouches)
            {
                if (!excluded.Contains(pairing) && reached.Contains((origin, signer)) && reached.Add(pairing))
                {
                    grew = true;
                }
            }
        }

        return reached;
    }
}
