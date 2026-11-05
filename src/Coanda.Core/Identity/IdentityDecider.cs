using Coanda.Core.Identity.Events;

namespace Coanda.Core.Identity;

/// <summary>
/// The rules for recording an identity event, as pure decisions: the event, or a
/// <see cref="DomainException"/> quoting the rule. Each judges over the identity records handed to it,
/// in <see cref="IdentityTrust"/>'s input form, with the root key the node trusts and the key that will
/// sign the new event, and refuses what <see cref="IdentityTrust"/> would not count. A caller hands in
/// records that already carry their sequence, this node's own included.
/// </summary>
public static class IdentityDecider
{
    /// <summary>
    /// Records the project's owner root: the trusted root key, for the node that records it. Refused when
    /// the key is not the trusted root key, since such a record would not count, and when the records
    /// already hold a root record that counts.
    /// </summary>
    public static OwnerRootRecorded RecordOwnerRoot(
        string trustedRoot, IReadOnlyCollection<IdentityRecord> records, Guid nodeId, string key, DateTimeOffset now)
    {
        RequireKey(key, "The owner root's key");
        if (key != trustedRoot)
        {
            throw new DomainException(
                $"The owner root's key {key} is not the trusted root key {trustedRoot}; a root record counts only when it names the trusted root key, which signs it.");
        }

        if (IdentityTrust.CountingRootNodes(trustedRoot, records) is [Guid existing, ..])
        {
            throw new DomainException(
                $"The project already has an owner root, node {existing} with key {trustedRoot}; a project has one owner root.");
        }

        return new OwnerRootRecorded(nodeId, key, now);
    }

    /// <summary>Revokes a node's key. Only a live root-set key revokes, and never a key that outranks it or its own.</summary>
    public static NodeRevoked RevokeNode(
        string trustedRoot, IReadOnlyCollection<IdentityRecord> records, string signer, Guid nodeId, string key, DateTimeOffset now)
    {
        RequireKey(key, "The revoked key");
        IdentityVerdict verdict = IdentityTrust.Evaluate(trustedRoot, records);
        int rank = RequireRootSetKey(verdict, signer, "revoke a node");
        RequireOutranked(verdict, signer, rank, key, "revoke");
        return new NodeRevoked(nodeId, key, now);
    }

    /// <summary>
    /// Names a successor: a node with the exact key a counting vouch gives it (a root-set key at or above
    /// the signer's rank vouching it directly, or else trust at that rank reaching it), which is not a voided successor, a member's root key, or already in the root
    /// set. Only a live root-set key names one.
    /// </summary>
    public static SuccessorNamed NameSuccessor(
        string trustedRoot, IReadOnlyCollection<IdentityRecord> records, string signer, Guid nodeId, string key, DateTimeOffset now)
    {
        RequireKey(key, "The successor's key");
        IdentityVerdict verdict = IdentityTrust.Evaluate(trustedRoot, records);
        RequireRootSetKey(verdict, signer, "name a successor");
        string? refusal = verdict.NominationOf(signer, nodeId) switch
        {
            null => $"Node {nodeId} has no counting vouch that gives it a key at the rank of {signer}, so it cannot be named a successor; a successor is named with the exact key a counting vouch gives its node.",
            { Key: string vouched } when vouched != key => $"Node {nodeId} was named a successor with the key {key}, but its counting vouch gives it {vouched}; a successor is named with the exact key a counting vouch gives its node.",
            { Standing: NominationStanding.Voided } => $"The key {key} was revoked as a successor, so it cannot be named one again; a successor revocation voids its key for good.",
            { Standing: NominationStanding.Member } => $"The key {key} is a member's root key, so it cannot be named a successor; no member joins the ranked root set.",
            { Standing: NominationStanding.Ranked } => $"Node {nodeId} with the key {key} is already in the ranked root set, so it cannot be named a successor.",
            _ => null,
        };

        return refusal is null
            ? new SuccessorNamed(nodeId, key, now)
            : throw new DomainException(refusal);
    }

    /// <summary>Revokes a successor's rank. Only a live root-set key revokes, and never a key that outranks it or its own.</summary>
    public static SuccessorRevoked RevokeSuccessor(
        string trustedRoot, IReadOnlyCollection<IdentityRecord> records, string signer, string successorKey, DateTimeOffset now)
    {
        RequireKey(successorKey, "The successor's key");
        IdentityVerdict verdict = IdentityTrust.Evaluate(trustedRoot, records);
        int rank = RequireRootSetKey(verdict, signer, "revoke a successor");
        RequireOutranked(verdict, signer, rank, successorKey, "revoke as a successor");
        return new SuccessorRevoked(successorKey, now);
    }

    /// <summary>
    /// Rotates the signer in as a root key, superseding the key that named it. The signer must be a
    /// successor a live root-set key named, and the key it supersedes must be its namer and not already
    /// superseded by a counting rotation.
    /// </summary>
    public static RootRotated Rotate(
        string trustedRoot, IReadOnlyCollection<IdentityRecord> records, string signer, string supersededKey, DateTimeOffset now)
    {
        RequireKey(supersededKey, "The superseded key");
        IdentityVerdict verdict = IdentityTrust.Evaluate(trustedRoot, records);
        IReadOnlyList<string> namers = verdict.NamersOf(signer);
        if (namers.Count == 0)
        {
            throw new DomainException(
                $"The key {signer} cannot rotate in: no live root key named it a successor; a rotation is signed by a successor a live root key named.");
        }

        if (!namers.Contains(supersededKey, StringComparer.Ordinal))
        {
            throw new DomainException(
                $"The key {signer} cannot supersede {supersededKey}: it was named by {string.Join(", ", namers)}; a rotation supersedes the key that named its successor.");
        }

        if (verdict.IsSuperseded(supersededKey))
        {
            throw new DomainException(
                $"The key {supersededKey} is already superseded by a counting rotation; a key is superseded once.");
        }

        return new RootRotated(signer, supersededKey, now);
    }

    /// <summary>
    /// Adds a member by its root node id and root key, with a role. Only a live root-set key adds one, and
    /// never a key or node of the ranked root set, since no member joins it.
    /// </summary>
    public static MemberAdded AddMember(
        string trustedRoot,
        IReadOnlyCollection<IdentityRecord> records,
        string signer,
        Guid rootNodeId,
        string rootKey,
        MemberRole role,
        DateTimeOffset now)
    {
        RequireKey(rootKey, "The member's root key");
        IdentityVerdict verdict = IdentityTrust.Evaluate(trustedRoot, records);
        RequireRootSetKey(verdict, signer, "add a member");
        if (verdict.RootSet.FirstOrDefault(k => k.Key == rootKey || k.NodeId == rootNodeId) is { } ranked)
        {
            throw new DomainException(
                $"Node {rootNodeId} with the key {rootKey} cannot be added as a member: the root-set key {ranked.Key} (rank {ranked.Rank}) holds node {ranked.NodeId}; no member joins the ranked root set, and a node and a key are bound one to one.");
        }

        return new MemberAdded(rootNodeId, rootKey, role, now);
    }

    /// <summary>Removes a member by its root key. Only a live root-set key removes one.</summary>
    public static MemberRemoved RemoveMember(
        string trustedRoot, IReadOnlyCollection<IdentityRecord> records, string signer, string rootKey, DateTimeOffset now)
    {
        RequireKey(rootKey, "The member's root key");
        RequireRootSetKey(IdentityTrust.Evaluate(trustedRoot, records), signer, "remove a member");
        return new MemberRemoved(rootKey, now);
    }

    private static void RequireKey(string key, string what)
    {
        if (!IdentityKeys.IsKey(key))
        {
            throw new DomainException($"{what} '{key}' is not a public key; a key is {IdentityKeys.Rule}.");
        }
    }

    private static int RequireRootSetKey(IdentityVerdict verdict, string signer, string act) =>
        verdict.RankOf(signer)
            ?? throw new DomainException(
                $"The key {signer} cannot {act}: it is not a live key of the ranked root set; only a live root-set key may {act}.");

    private static void RequireOutranked(IdentityVerdict verdict, string signer, int rank, string key, string act)
    {
        if (key == signer)
        {
            throw new DomainException($"The key {signer} cannot {act} itself; a root-set key may not revoke a key that outranks it, nor itself.");
        }

        if (verdict.RankOf(key) is int target && target < rank)
        {
            throw new DomainException(
                $"The key {signer} (rank {rank}) cannot {act} {key} (rank {target}): that key outranks it; a root-set key may not revoke a key that outranks it, nor itself.");
        }
    }
}
