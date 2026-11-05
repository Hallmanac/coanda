namespace Coanda.Core.Identity.Events;

/// <summary>
/// A successor's rank was revoked, named by its key. Every root-set act its key signed is void,
/// whenever it signed it, its rotation included; the vouch that admitted its node, and the vouches
/// its key signed, stand.
/// </summary>
public sealed record SuccessorRevoked(string SuccessorKey, DateTimeOffset RevokedAt) : IIdentityEvent;
