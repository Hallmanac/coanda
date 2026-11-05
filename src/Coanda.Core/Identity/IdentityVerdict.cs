namespace Coanda.Core.Identity;

/// <summary>
/// What <see cref="IdentityTrust.Evaluate"/> settled from one set of identity records: whether a key
/// may sign for an origin and why not, the ranked root set, and a member key's role.
/// </summary>
public sealed class IdentityVerdict
{
    private readonly string trustedRoot;
    private readonly IReadOnlyList<Guid> rootNodes;
    private readonly Acts acts;
    private readonly TrustGraph trust;
    private readonly IReadOnlyDictionary<string, List<string>> namers;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, Nomination>> nominations;

    private IdentityVerdict(
        string trustedRoot,
        IReadOnlyList<Guid> rootNodes,
        IReadOnlyList<RankedKey> rootSet,
        Acts acts,
        TrustGraph trust,
        IReadOnlyDictionary<string, List<string>> namers,
        IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, Nomination>> nominations)
    {
        this.trustedRoot = trustedRoot;
        this.rootNodes = rootNodes;
        RootSet = rootSet;
        this.acts = acts;
        this.trust = trust;
        this.namers = namers;
        this.nominations = nominations;
    }

    /// <summary>Whether the records hold a root record signed by the trusted root key.</summary>
    public bool HasRoot => rootNodes.Count > 0;

    /// <summary>The live keys of the ranked root set, each with its rank, from rank 0 (the original root) down.</summary>
    public IReadOnlyList<RankedKey> RootSet { get; }

    internal static IdentityVerdict WithoutRoot(string trustedRoot) =>
        new(trustedRoot, [], [], new Acts(), TrustGraph.Compute([], [], new Acts()), new Dictionary<string, List<string>>(), NoNominations);

    internal static IdentityVerdict WithConflictedRoot(string trustedRoot, IReadOnlyList<Guid> rootNodes, TrustGraph trust) =>
        new(trustedRoot, rootNodes, [], new Acts(), trust, new Dictionary<string, List<string>>(), NoNominations);

    internal static IdentityVerdict Settled(
        string trustedRoot,
        IReadOnlyList<RankedKey> rootSet,
        Acts acts,
        TrustGraph trust,
        IReadOnlyDictionary<string, List<string>> namers,
        IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, Nomination>> nominations) =>
        new(trustedRoot, [rootSet[0].NodeId], [.. rootSet], acts, trust, namers, nominations);

    /// <summary>Whether <paramref name="key"/> may sign for the origin <paramref name="origin"/>, and when it may not, why.</summary>
    public SigningAnswer MaySign(string key, Guid origin) =>
        (HasRoot, RootSet.Count) switch
        {
            (false, _) => SigningAnswer.Refused(SigningRefusal.NoRoot),
            (true, 0) when key == trustedRoot && rootNodes.Contains(origin) => SigningAnswer.Refused(SigningRefusal.Conflicted),
            _ when acts.Revoked.Contains(key) => SigningAnswer.Refused(SigningRefusal.Revoked),
            _ when acts.IsRemovedMember(key) => SigningAnswer.Refused(SigningRefusal.MemberRemoved),
            _ when trust.Trusts(origin, key) => SigningAnswer.Allowed,
            _ when trust.IsConflicted(origin, key) => SigningAnswer.Refused(SigningRefusal.Conflicted),
            _ when trust.IsProposed(origin, key) => SigningAnswer.Refused(SigningRefusal.VouchersMayNotSign),
            _ => SigningAnswer.Refused(SigningRefusal.NoCountingVouch),
        };

    /// <summary>The rank of <paramref name="key"/> in the ranked root set, or null when it is not a live key of it.</summary>
    public int? RankOf(string key) => RootSet.FirstOrDefault(k => k.Key == key)?.Rank;

    /// <summary>
    /// The role of the member whose root key is <paramref name="key"/>: the role of the add signed by the
    /// highest-ranked key, and among that key's adds the latest in its chain. Null when no add counts or
    /// the member was removed.
    /// </summary>
    public MemberRole? RoleOf(string key) =>
        acts.IsRemovedMember(key)
            ? null
            : acts.Adds
                .Where(a => a.Key == key)
                .OrderBy(a => a.SignerRank)
                .ThenByDescending(a => a.Sequence)
                .Select(a => (MemberRole?)a.Role)
                .FirstOrDefault();

    /// <summary>
    /// What the live root-set key <paramref name="namer"/> may name for <paramref name="nodeId"/>: the key
    /// a direct vouch from a root-set key at or above its rank gives that node, or failing one the key
    /// trust at its rank gives it, and whether naming that pairing counts. Null when neither gives the
    /// node a key, or <paramref name="namer"/> is not a live root-set key.
    /// </summary>
    internal Nomination? NominationOf(string namer, Guid nodeId) =>
        nominations.TryGetValue(namer, out IReadOnlyDictionary<Guid, Nomination>? nameable)
            ? nameable.GetValueOrDefault(nodeId)
            : null;

    /// <summary>The live root-set keys whose successor naming of <paramref name="key"/> counts.</summary>
    public IReadOnlyList<string> NamersOf(string key) =>
        namers.TryGetValue(key, out List<string>? keys)
            ? keys
            : [];

    /// <summary>Whether a counting rotation already supersedes <paramref name="key"/>: some live root-set key ranks one below it.</summary>
    public bool IsSuperseded(string key) =>
        RankOf(key) is int rank && RootSet.Any(k => k.Rank == rank + 1);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<Guid, Nomination>> NoNominations { get; } =
        new Dictionary<string, IReadOnlyDictionary<Guid, Nomination>>();
}
