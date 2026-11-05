namespace Coanda.Core.Identity.Events;

/// <summary>A member was removed, named by the member's root key. A removal stands in this version, whatever adds are recorded.</summary>
public sealed record MemberRemoved(string RootKey, DateTimeOffset RemovedAt) : IIdentityEvent;
