namespace Coanda.Core.Parcels;

/// <summary>
/// One claim on a parcel as this node stores it: the node it names; the hash of the ledger line that carries
/// it, or null for this node's own claim before the export has put it on a line; whether this node recorded
/// it; and, for this node's own claim, whether a push of this node's ledger carrying it succeeded and a fetch
/// of its peers' ledgers after that push succeeded too.
/// </summary>
public sealed record StoredClaim(Guid Node, string? LineHash, bool IsOwn, bool IsPublished);
