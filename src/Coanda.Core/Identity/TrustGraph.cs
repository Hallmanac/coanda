using Coanda.Core.Identity.Events;

namespace Coanda.Core.Identity;

/// <summary>
/// Who may sign for which node, for one state of the acts: the pairings trust reaches, those in
/// conflict, and the pairings the vouches propose.
/// </summary>
internal sealed class TrustGraph
{
    private readonly HashSet<(Guid NodeId, string Key)> trusted;
    private readonly HashSet<(Guid NodeId, string Key)> conflicted;
    private readonly HashSet<(Guid NodeId, string Key)> proposed;

    private TrustGraph(
        HashSet<(Guid NodeId, string Key)> trusted,
        HashSet<(Guid NodeId, string Key)> conflicted,
        HashSet<(Guid NodeId, string Key)> proposed)
    {
        this.trusted = trusted;
        this.conflicted = conflicted;
        this.proposed = proposed;
    }

    public static TrustGraph Compute(IdentityRecord[] all, IReadOnlyList<RankedKey> chain, Acts acts)
    {
        HashSet<(Guid NodeId, string Key)> bound = Bound(chain, acts);
        HashSet<(Guid NodeId, string Key)> anchors =
        [
            .. chain.Select(k => (k.NodeId, k.Key)),
            .. acts.Adds.Where(a => !acts.MayNotSign(a.Key)).Select(a => (a.NodeId, a.Key)),
        ];

        (Guid Origin, string Signer, (Guid NodeId, string Key) Pairing)[] proposals =
        [
            .. IdentityTrust.Of<NodeVouched>(all)
                .Select(r => (r.Record.Origin, r.Record.Signer, Pairing: (r.Event.NodeId, r.Event.Key)))
                .Where(v => !Breaks(bound, v.Pairing)),
        ];

        HashSet<(Guid NodeId, string Key)> proposed = [.. proposals.Select(v => v.Pairing)];

        (Guid Origin, string Signer, (Guid NodeId, string Key) Pairing)[] vouches =
        [
            .. proposals.Where(v => !acts.MayNotSign(v.Signer) && !acts.MayNotSign(v.Pairing.Key)),
        ];

        HashSet<(Guid NodeId, string Key)> reached = Follow(anchors, vouches, []);
        HashSet<(Guid NodeId, string Key)> conflicted = Conflicts(reached);

        HashSet<(Guid NodeId, string Key)> trusted = Follow(anchors, vouches, conflicted);
        return new TrustGraph(trusted, conflicted, proposed);
    }

    /// <summary>Whether <paramref name="key"/> may sign for <paramref name="nodeId"/>.</summary>
    public bool Trusts(Guid nodeId, string key) => trusted.Contains((nodeId, key));

    public bool IsConflicted(Guid nodeId, string key) => conflicted.Contains((nodeId, key));

    /// <summary>Whether some vouch pairs <paramref name="nodeId"/> with <paramref name="key"/> without breaking a binding.</summary>
    public bool IsProposed(Guid nodeId, string key) => proposed.Contains((nodeId, key));

    /// <summary>
    /// The pairings the keys of <paramref name="chain"/> vouched directly, each from the node its rank was
    /// settled on, keyed by node id: the vouches a successor's standing is read from. Only those keys'
    /// vouches take part, conflicts included, so no key outside the chain can make or break a successor;
    /// a vouch naming a revoked key, or breaking a binding, does not count, and pairings that share a node
    /// id or a key give neither.
    /// </summary>
    public static Dictionary<Guid, string> RootSetVouches(IdentityRecord[] all, IReadOnlyList<RankedKey> chain, Acts acts)
    {
        HashSet<(Guid NodeId, string Key)> bound = Bound(chain, acts);
        HashSet<(Guid NodeId, string Key)> vouched =
        [
            .. IdentityTrust.Of<NodeVouched>(all)
                .Where(r => chain.Any(k => k.Key == r.Record.Signer && k.NodeId == r.Record.Origin))
                .Select(r => (r.Event.NodeId, r.Event.Key))
                .Where(p => !acts.MayNotSign(p.Key) && !Breaks(bound, p)),
        ];

        HashSet<(Guid NodeId, string Key)> conflicted = Conflicts(vouched);
        return vouched.Where(p => !conflicted.Contains(p)).ToDictionary(p => p.NodeId, p => p.Key);
    }

    /// <summary>Pairings bound by a root-set key's rank or by a member add; a vouch may not pair either side with another.</summary>
    private static HashSet<(Guid NodeId, string Key)> Bound(IReadOnlyList<RankedKey> chain, Acts acts) =>
    [
        .. chain.Select(k => (k.NodeId, k.Key)),
        .. acts.Adds.Select(a => (a.NodeId, a.Key)),
    ];

    private static bool Breaks(HashSet<(Guid NodeId, string Key)> bound, (Guid NodeId, string Key) pairing) =>
        bound.Any(b => (b.NodeId == pairing.NodeId) != (b.Key == pairing.Key));

    private static HashSet<(Guid NodeId, string Key)> Conflicts(HashSet<(Guid NodeId, string Key)> pairings) =>
    [
        .. pairings.Where(p => pairings.Any(o => o != p && (o.NodeId == p.NodeId || o.Key == p.Key))),
    ];

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
