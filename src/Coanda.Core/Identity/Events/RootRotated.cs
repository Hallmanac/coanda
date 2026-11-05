namespace Coanda.Core.Identity.Events;

/// <summary>
/// A named successor rotated in: its key and the key it supersedes, which is the key that named it.
/// The successor ranks one below the key it supersedes, beside it in the ranked root set rather than
/// in place of it.
/// </summary>
public sealed record RootRotated(string SuccessorKey, string SupersededKey, DateTimeOffset RotatedAt) : IIdentityEvent;
