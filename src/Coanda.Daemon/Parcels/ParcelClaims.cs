using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events;

namespace Coanda.Daemon.Parcels;

/// <summary>
/// The claims this node stores on a parcel, read from the store alone, so every service given only the store
/// reads them alike: each claim on the parcel's stream, with the hash of the ledger line that carries it, whether
/// this node recorded it (<see cref="ImportedEvents"/>), and for this node's own claim whether its line was pushed
/// before a fetch that succeeded (<see cref="LedgerProgress"/>). A claim from another origin counts only when that
/// origin is the node it names. What the claims mean is <see cref="ClaimDecider"/>'s.
/// </summary>
public static class ParcelClaims
{
    /// <summary>The claims on <paramref name="parcel"/> that count, in the order this node stored them; none when it is unclaimed.</summary>
    public static async Task<IReadOnlyList<StoredClaim>> StoredAsync(IDocumentStore store, IQuerySession session, Parcel parcel, CancellationToken cancellationToken)
    {
        if (!parcel.IsClaimed)
        {
            return [];
        }

        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcel.Id, token: cancellationToken);
        IEvent[] claims = [.. events.Where(e => e.Data is ParcelClaimed)];
        IReadOnlyDictionary<Guid, LedgerLine> lines = await ImportedEvents.LinesAsync(session, [.. claims.Select(e => e.Id)], cancellationToken);
        Guid? self = lines.Count > 0
            ? await ImportedEvents.SelfAsync(store, cancellationToken)
            : null;
        long fetched = await LedgerProgress.ThroughAsync(session, LedgerProgress.FetchedId, cancellationToken);

        List<StoredClaim> stored = [];
        foreach (IEvent entry in claims)
        {
            var claimed = (ParcelClaimed)entry.Data;
            if (!lines.TryGetValue(entry.Id, out LedgerLine? line))
            {
                stored.Add(new StoredClaim(claimed.NodeId, null, IsOwn: true, IsPublished: false));
            }
            else if (line.Origin == claimed.NodeId)
            {
                bool own = line.Origin == self;
                stored.Add(new StoredClaim(claimed.NodeId, line.Hash, own, IsPublished: own && line.Sequence <= fetched));
            }
        }

        return stored;
    }

    /// <summary>Where <paramref name="parcel"/>'s claims leave this node, as the store holds them now.</summary>
    public static async Task<ClaimStanding> StandingAsync(IDocumentStore store, IQuerySession session, Parcel parcel, CancellationToken cancellationToken) =>
        ClaimDecider.Standing(await StoredAsync(store, session, parcel, cancellationToken));
}
