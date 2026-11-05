namespace Coanda.Core.Identity;

/// <summary>
/// What a root-set key may name for one node: the key a direct vouch from a root-set key at or above
/// its rank gives that node, or failing one the key trust gives it, and whether naming that pairing
/// would count.
/// </summary>
internal sealed record Nomination(string Key, NominationStanding Standing);
