namespace Coanda.Core.Identity;

/// <summary>The root-set acts that count so far.</summary>
internal sealed class Acts
{
    public HashSet<string> Revoked { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Voided { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);
    public List<MemberAdd> Adds { get; } = [];

    public bool IsMemberKey(string key) => Adds.Any(a => a.Key == key);

    /// <summary>Whether <paramref name="key"/> is the root key of a member that was added and removed; a removal names no one else.</summary>
    public bool IsRemovedMember(string key) => Removed.Contains(key) && IsMemberKey(key);

    /// <summary>Whether nothing <paramref name="key"/> signs counts: it is revoked, or a removed member's root key.</summary>
    public bool MayNotSign(string key) => Revoked.Contains(key) || IsRemovedMember(key);
}
