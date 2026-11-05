namespace Coanda.Core.Identity;

/// <summary>A member add that counts: the member's pairing and role, and the rank and chain sequence of the add's signer.</summary>
internal sealed record MemberAdd(Guid NodeId, string Key, MemberRole Role, int SignerRank, long Sequence);
