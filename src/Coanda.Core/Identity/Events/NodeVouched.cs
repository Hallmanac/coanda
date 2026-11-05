namespace Coanda.Core.Identity.Events;

/// <summary>A node was vouched for: its id and the exact public key vouched for it.</summary>
public sealed record NodeVouched(Guid NodeId, string Key, DateTimeOffset VouchedAt) : IIdentityEvent;
