namespace Coanda.Core.Identity.Events;

/// <summary>
/// A node's key was revoked: the node's id and its key. A revoked key stays revoked in this version,
/// and every record it signed stops counting, whenever it was signed.
/// </summary>
public sealed record NodeRevoked(Guid NodeId, string Key, DateTimeOffset RevokedAt) : IIdentityEvent;
