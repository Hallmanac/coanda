using Coanda.Core.Identity.Events;

namespace Coanda.Core.Identity;

/// <summary>
/// The one trust rule over a project's identity records, as a pure function: given the root key the
/// reader trusts and a set of identity records, it answers whether a key may sign for an origin and why
/// not, which keys form the ranked root set, and a member key's role. It reads the records as a set, so
/// the same records give the same answers in any arrival order.
/// </summary>
/// <remarks>
/// <para>
/// The root record counts when the trusted root key signed it, it names that key, and its origin is the
/// node it names. The ranked root set is then settled one rank at a time from the original root (rank
/// 0): the acts of the key at rank r (node revocations, successor namings, successor revocations, member
/// changes) are applied, and the key at rank r + 1 is the successor that key named earliest in its own
/// chain whose rotation superseding it counts. A named successor counts only when a key at rank r or
/// above vouched its node directly with the named key, read from those keys' vouches alone, conflicts
/// included, so no vouch from outside the root set can make or unseat a successor. Because a
/// revocation of a key that outranks its signer, or of the signer itself, is ignored, no key's acts can
/// undo the standing of a key that outranks it or its own, which is what keeps the rule well founded: a
/// key's standing is settled by the keys above it alone. A live key of the ranked root set may sign for
/// the node its rank was settled on.
/// </para>
/// <para>
/// Trust follows vouches transitively from the root-set keys and from the root keys of members a
/// root-set key added and none removed. A record counts only when the key that signed it may sign for the
/// record's own origin, and nothing a revoked key or a removed member's root key signed counts. Node ids
/// and keys are bound one to one: a vouch that pairs a root-set or member node id with another key, or
/// such a key with another node id, does not count, and when counting vouches pair one node id with two
/// keys, or one key with two node ids, none of those pairings may sign. The conflicts are read from every
/// pairing trust could reach before any conflict is settled, and trust is then followed again without
/// the conflicted pairings, so a conflict also cuts off what was vouched only through it.
/// </para>
/// </remarks>
public static class IdentityTrust
{
    public static IdentityVerdict Evaluate(string trustedRoot, IEnumerable<IdentityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(trustedRoot);
        ArgumentNullException.ThrowIfNull(records);
        IdentityRecord[] all = [.. records];

        Guid[] rootNodes = CountingRootNodes(trustedRoot, all);

        return rootNodes switch
        {
            [] => IdentityVerdict.WithoutRoot(trustedRoot),
            [Guid rootNode] => Settle(trustedRoot, rootNode, all),
            _ => IdentityVerdict.WithConflictedRoot(trustedRoot, rootNodes, TrustGraph.Compute(all, [], new Acts())),
        };
    }

    /// <summary>The node ids of the root records that count: signed by the trusted root key, naming it, from the node they name.</summary>
    internal static Guid[] CountingRootNodes(string trustedRoot, IEnumerable<IdentityRecord> records) =>
    [
        .. Of<OwnerRootRecorded>(records)
            .Where(r => r.Record.Signer == trustedRoot && r.Event.Key == trustedRoot && r.Event.NodeId == r.Record.Origin)
            .Select(r => r.Event.NodeId)
            .Distinct()
            .Order(),
    ];

    private static IdentityVerdict Settle(string trustedRoot, Guid rootNode, IdentityRecord[] all)
    {
        List<RankedKey> chain = [new RankedKey(trustedRoot, 0, rootNode)];
        var acts = new Acts();
        var namers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var nominations = new Dictionary<string, IReadOnlyDictionary<Guid, Nomination>>(StringComparer.Ordinal);

        while (true)
        {
            RankedKey current = chain[^1];
            ApplyActs(current, chain, all, acts);

            // What the current key may name: the pairings the root set so far vouched, read from its vouches alone.
            Dictionary<Guid, Nomination> nameable = TrustGraph.RootSetVouches(all, chain, acts)
                .ToDictionary(v => v.Key, v => new Nomination(v.Value, StandingOf(v.Key, v.Value, chain, acts)));
            nominations[current.Key] = nameable;

            (Guid NodeId, string Key, long Sequence)[] named =
            [
                .. Of<SuccessorNamed>(all)
                    .Where(r => r.Record.Signer == current.Key && r.Record.Origin == current.NodeId)
                    .Where(r => nameable.TryGetValue(r.Event.NodeId, out Nomination? nomination)
                        && nomination == new Nomination(r.Event.Key, NominationStanding.Nameable))
                    .Select(r => (r.Event.NodeId, r.Event.Key, r.Record.Sequence)),
            ];

            foreach (string key in named.Select(n => n.Key).Distinct(StringComparer.Ordinal))
            {
                if (!namers.TryGetValue(key, out List<string>? keys))
                {
                    keys = [];
                    namers[key] = keys;
                }

                keys.Add(current.Key);
            }

            // Of the successors whose rotation superseding the current key counts, the one named earliest in its chain wins.
            (Guid NodeId, string Key, long Sequence)? winner = named
                .Where(n => Of<RootRotated>(all).Any(r =>
                    r.Record.Signer == n.Key
                    && r.Record.Origin == n.NodeId
                    && r.Event.SuccessorKey == n.Key
                    && r.Event.SupersededKey == current.Key))
                .OrderBy(n => n.Sequence)
                .ThenBy(n => n.Key, StringComparer.Ordinal)
                .Cast<(Guid NodeId, string Key, long Sequence)?>()
                .FirstOrDefault();

            if (winner is not { } next)
            {
                return IdentityVerdict.Settled(trustedRoot, chain, acts, TrustGraph.Compute(all, chain, acts), namers, nominations);
            }

            chain.Add(new RankedKey(next.Key, current.Rank + 1, next.NodeId));
        }
    }

    private static NominationStanding StandingOf(Guid nodeId, string key, List<RankedKey> chain, Acts acts) =>
        (acts.Voided.Contains(key), acts.IsMemberKey(key), chain.Any(k => k.Key == key || k.NodeId == nodeId)) switch
        {
            (true, _, _) => NominationStanding.Voided,
            (_, true, _) => NominationStanding.Member,
            (_, _, true) => NominationStanding.Ranked,
            _ => NominationStanding.Nameable,
        };

    /// <summary>
    /// Applies the root-set acts <paramref name="current"/> signed from its own node. A revocation of a
    /// key already in the chain is ignored, since every such key outranks the current one or is it.
    /// </summary>
    private static void ApplyActs(RankedKey current, List<RankedKey> chain, IdentityRecord[] all, Acts acts)
    {
        foreach (IdentityRecord record in all.Where(r => r.Signer == current.Key && r.Origin == current.NodeId))
        {
            switch (record.Event)
            {
                case NodeRevoked revoked when !chain.Any(k => k.Key == revoked.Key):
                    acts.Revoked.Add(revoked.Key);
                    break;
                case SuccessorRevoked revoked when !chain.Any(k => k.Key == revoked.SuccessorKey):
                    acts.Voided.Add(revoked.SuccessorKey);
                    break;
                case MemberRemoved removed:
                    acts.Removed.Add(removed.RootKey);
                    break;
                case MemberAdded added when !chain.Any(k => k.Key == added.RootKey || k.NodeId == added.RootNodeId):
                    acts.Adds.Add(new MemberAdd(added.RootNodeId, added.RootKey, added.Role, current.Rank, record.Sequence));
                    break;
            }
        }
    }

    internal static IEnumerable<(IdentityRecord Record, T Event)> Of<T>(IEnumerable<IdentityRecord> records)
        where T : IIdentityEvent
    {
        foreach (IdentityRecord record in records)
        {
            if (record.Event is T typed)
            {
                yield return (record, typed);
            }
        }
    }
}
