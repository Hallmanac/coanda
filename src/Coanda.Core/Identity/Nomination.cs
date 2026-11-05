namespace Coanda.Core.Identity;

/// <summary>
/// What a root-set key may name for one node: the key a counting vouch from a root-set key at or above
/// its rank gives that node, and whether naming that pairing would count.
/// </summary>
internal sealed record Nomination(string Key, NominationStanding Standing);
