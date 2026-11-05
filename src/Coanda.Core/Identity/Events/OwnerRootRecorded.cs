namespace Coanda.Core.Identity.Events;

/// <summary>
/// The project's owner root was recorded: the root node's id and its public key, which is the owner's
/// root key (the root node's own signing key, not a separate key). Keys are 32 bytes in standard
/// base64 with padding. It binds that node id and that key to each other, one to one.
/// </summary>
public sealed record OwnerRootRecorded(Guid NodeId, string Key, DateTimeOffset RecordedAt) : IIdentityEvent;
