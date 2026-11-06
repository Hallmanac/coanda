using Coanda.Core.Parcels.Events;

namespace Coanda.Core.Parcels;

/// <summary>
/// The rules of a parcel's claims (design section 6). There are no locks and no leases: two nodes that claim
/// one parcel without seeing each other's claim both record one, and the winner is the claim whose ledger
/// line has the lowest line hash, which every node holding both lines computes alike. A node acts on its
/// claim only once it holds the parcel, after a push of its ledger carrying the claim and a fetch of its
/// peers' ledgers after that push, so a peer that claimed first has had the chance to arrive. On a claimed
/// parcel only the holder starts a flow run; on an unclaimed one, a node triggers only on what it recorded.
/// </summary>
public static class ClaimDecider
{
    private const string HashPrefix = "sha256:";

    /// <summary>
    /// Claims <paramref name="parcel"/> for <paramref name="nodeId"/>, this node, given the claims this node
    /// stores on it, or null when this node has already claimed it, so a claim made again records nothing.
    /// A closed parcel is claimed by nobody, and a node that has seen another node's claim before making one
    /// of its own makes none, so any two claims stored on one parcel were made by nodes that had not seen each other's.
    /// </summary>
    public static ParcelClaimed? Claim(Parcel parcel, IReadOnlyCollection<StoredClaim> stored, Guid nodeId, string claimedBy, DateTimeOffset now)
    {
        if (parcel.IsClosed)
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is closed ({parcel.Resolution}); a closed parcel is claimed by nobody.");
        }

        if (stored.Any(c => c.IsOwn))
        {
            return null;
        }

        if (stored.FirstOrDefault() is { } other)
        {
            throw new DomainException(
                $"Parcel {Ids.Short(parcel.Id)} is already claimed by node {other.Node}, and this node has no claim of its own on it; a node claims a parcel only before it has seen another node's claim.");
        }

        return new ParcelClaimed(parcel.Id, nodeId, claimedBy, now);
    }

    /// <summary>
    /// The winning claim among <paramref name="claims"/>: the one whose line hash, the full SHA-256 digest the
    /// line format defines, is lowest compared as unsigned bytes, or null when there are none. The order of
    /// the input never changes the answer: two claims on one line hash are the same line, and the node id
    /// breaks the tie only to keep the answer total.
    /// </summary>
    public static StoredClaim? Winner(IEnumerable<StoredClaim> claims)
    {
        StoredClaim? winner = null;
        byte[]? lowest = null;
        foreach (StoredClaim claim in claims)
        {
            byte[] digest = DigestOf(claim);
            int compared = lowest is null
                ? -1
                : digest.AsSpan().SequenceCompareTo(lowest);
            if (compared < 0 || (compared == 0 && winner is not null && claim.Node.CompareTo(winner.Node) < 0))
            {
                winner = claim;
                lowest = digest;
            }
        }

        return winner;
    }

    /// <summary>
    /// Where <paramref name="claims"/>, every claim this node stores on a parcel, leave this node. With none the
    /// parcel is unclaimed. While this node's own claim is on no line yet, no winner can be named, so it is not
    /// yet held; otherwise the winner is <see cref="Winner"/>'s, and this node holds the parcel when that is its
    /// own claim and the claim has been published and its peers fetched since.
    /// </summary>
    public static ClaimStanding Standing(IReadOnlyCollection<StoredClaim> claims)
    {
        if (claims.Count == 0)
        {
            return new ClaimStanding.Unclaimed();
        }

        if (claims.Any(c => c.IsOwn && c.LineHash is null))
        {
            return new ClaimStanding.NotYetHeld("this node's claim is not on a line of its ledger yet, so it has not been pushed");
        }

        StoredClaim winner = Winner(claims) ?? throw new InvalidOperationException("A non-empty set of claims has a winner.");
        return winner switch
        {
            { IsOwn: false } => new ClaimStanding.Lost(winner.Node),
            { IsPublished: true } => new ClaimStanding.Held(),
            _ => new ClaimStanding.NotYetHeld("no push of this node's ledger carrying its claim, followed by a fetch of its peers' ledgers, has succeeded yet"),
        };
    }

    /// <summary>
    /// Whether the trigger loop starts a flow run on a parcel standing at <paramref name="standing"/> for an
    /// event, recorded by this node when <paramref name="ownEvent"/>: on a parcel this node holds, whichever
    /// node recorded the event; on an unclaimed one, only for an event of this node's; on a parcel another
    /// node's claim wins or this node does not hold yet, never.
    /// </summary>
    public static bool Triggers(ClaimStanding standing, bool ownEvent) => standing switch
    {
        ClaimStanding.Held => true,
        ClaimStanding.Unclaimed => ownEvent,
        _ => false,
    };

    /// <summary>
    /// A flow run starts on a claimed parcel only on the node that holds it, whoever asks: refused with the
    /// winning node's name, or with why this node's own claim is not yet held.
    /// </summary>
    public static void RequireMayStart(Guid parcelId, ClaimStanding standing)
    {
        switch (standing)
        {
            case ClaimStanding.Lost lost:
                throw new DomainException(
                    $"Parcel {Ids.Short(parcelId)} is claimed, and node {lost.Winner}'s claim wins it; on a claimed parcel only the node holding it starts a flow run.");
            case ClaimStanding.NotYetHeld pending:
                throw new DomainException(
                    $"Parcel {Ids.Short(parcelId)} is claimed, and this node's own claim on it is not yet held: {pending.Reason}. On a claimed parcel only the node holding it starts a flow run.");
        }
    }

    private static byte[] DigestOf(StoredClaim claim)
    {
        string hash = claim.LineHash
            ?? throw new ArgumentException($"Node {claim.Node}'s claim is on no line, so it has no line hash to be weighed by.", nameof(claim));
        return hash.StartsWith(HashPrefix, StringComparison.Ordinal) && hash.Length == HashPrefix.Length + 64
            ? Convert.FromHexString(hash.AsSpan(HashPrefix.Length))
            : throw new ArgumentException($"'{hash}' is not a line hash: sha256: and 64 hex digits.", nameof(claim));
    }
}
