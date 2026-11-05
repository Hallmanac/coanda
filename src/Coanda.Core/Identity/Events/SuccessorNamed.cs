namespace Coanda.Core.Identity.Events;

/// <summary>
/// A root key named a successor: a vouched node's id and the exact key already vouched for it. The
/// successor takes a rank only once its own rotation lands.
/// </summary>
public sealed record SuccessorNamed(Guid NodeId, string Key, DateTimeOffset NamedAt) : IIdentityEvent;
