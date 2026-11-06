namespace Coanda.Core.Parcels.Events;

/// <summary>
/// A node claimed the parcel, so that only it starts flow runs on it (design section 6). Two nodes that
/// claim one parcel without seeing each other's claim both record one, and every node names the same
/// winner, the claim whose ledger line has the lowest line hash (<see cref="ClaimDecider.Winner"/>).
/// A claim counts only when the node that recorded it is the node it names.
/// </summary>
public sealed record ParcelClaimed(
    Guid ParcelId,
    Guid NodeId,
    string ClaimedBy,
    DateTimeOffset ClaimedAt);
