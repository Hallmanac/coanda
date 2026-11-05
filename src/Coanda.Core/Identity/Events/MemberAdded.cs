namespace Coanda.Core.Identity.Events;

/// <summary>
/// A member was added: the member's root node id, the member's root key, and the role the member
/// holds. It binds that node id and that key to each other, one to one.
/// </summary>
public sealed record MemberAdded(Guid RootNodeId, string RootKey, MemberRole Role, DateTimeOffset AddedAt) : IIdentityEvent;
