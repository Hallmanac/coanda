using Coanda.Core;
using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using static Coanda.Tests.Core.Identity.Parties;

namespace Coanda.Tests.Core.Identity;

public sealed class IdentityDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The root, with S rotated in at rank 1 and A an ordinary vouched node.</summary>
    private static IReadOnlyList<IdentityRecord> Ranked() =>
        new Ledger().Root(Root).Vouch(Root, S).Vouch(Root, A).Name(Root, S).Rotate(S, Root).Records;

    private static void RefusedWith(string expected, Action decide) =>
        Assert.Equal(expected, Assert.Throws<DomainException>(decide).Message);

    [Fact]
    public void An_owner_root_is_recorded_in_a_project_with_none()
    {
        Assert.Equal(new OwnerRootRecorded(Root.Node, Root.Key, Now), IdentityDecider.RecordOwnerRoot([], Root.Node, Root.Key, Now));
    }

    [Fact]
    public void A_second_owner_root_is_refused()
    {
        RefusedWith(
            $"The project already has an owner root, node {Root.Node} with key {Root.Key}; a project has one owner root.",
            () => IdentityDecider.RecordOwnerRoot(new Ledger().Root(Root).Records, X.Node, X.Key, Now));
    }

    [Fact]
    public void A_key_that_is_not_32_bytes_of_padded_base64_is_refused()
    {
        RefusedWith(
            "The owner root's key 'AAAA' is not a public key; a key is 32 bytes in standard base64 with padding.",
            () => IdentityDecider.RecordOwnerRoot([], Root.Node, "AAAA", Now));
        string unpadded = Root.Key.TrimEnd('=');
        RefusedWith(
            $"The revoked key '{unpadded}' is not a public key; a key is 32 bytes in standard base64 with padding.",
            () => IdentityDecider.RevokeNode(Root.Key, Ranked(), Root.Key, A.Node, unpadded, Now));
    }

    [Fact]
    public void Root_set_acts_are_recorded_from_a_live_root_set_key()
    {
        IReadOnlyList<IdentityRecord> records = Ranked();

        Assert.Equal(new NodeRevoked(A.Node, A.Key, Now), IdentityDecider.RevokeNode(Root.Key, records, S.Key, A.Node, A.Key, Now));
        Assert.Equal(new SuccessorRevoked(S.Key, Now), IdentityDecider.RevokeSuccessor(Root.Key, records, Root.Key, S.Key, Now));
        Assert.Equal(new SuccessorNamed(A.Node, A.Key, Now), IdentityDecider.NameSuccessor(Root.Key, records, S.Key, A.Node, A.Key, Now));
        Assert.Equal(new MemberAdded(M.Node, M.Key, MemberRole.Owner, Now), IdentityDecider.AddMember(Root.Key, records, S.Key, M.Node, M.Key, MemberRole.Owner, Now));
        Assert.Equal(new MemberRemoved(M.Key, Now), IdentityDecider.RemoveMember(Root.Key, records, Root.Key, M.Key, Now));
    }

    [Fact]
    public void Root_set_acts_from_a_key_outside_the_ranked_root_set_are_refused()
    {
        IReadOnlyList<IdentityRecord> records = Ranked();

        RefusedWith(
            $"The key {A.Key} cannot revoke a node: it is not a live key of the ranked root set; only a live root-set key may revoke a node.",
            () => IdentityDecider.RevokeNode(Root.Key, records, A.Key, S.Node, S.Key, Now));
        RefusedWith(
            $"The key {A.Key} cannot name a successor: it is not a live key of the ranked root set; only a live root-set key may name a successor.",
            () => IdentityDecider.NameSuccessor(Root.Key, records, A.Key, S.Node, S.Key, Now));
        RefusedWith(
            $"The key {A.Key} cannot revoke a successor: it is not a live key of the ranked root set; only a live root-set key may revoke a successor.",
            () => IdentityDecider.RevokeSuccessor(Root.Key, records, A.Key, S.Key, Now));
        RefusedWith(
            $"The key {A.Key} cannot add a member: it is not a live key of the ranked root set; only a live root-set key may add a member.",
            () => IdentityDecider.AddMember(Root.Key, records, A.Key, M.Node, M.Key, MemberRole.Member, Now));
        RefusedWith(
            $"The key {A.Key} cannot remove a member: it is not a live key of the ranked root set; only a live root-set key may remove a member.",
            () => IdentityDecider.RemoveMember(Root.Key, records, A.Key, M.Key, Now));
    }

    [Fact]
    public void A_voided_successor_is_no_longer_a_live_root_set_key()
    {
        IReadOnlyList<IdentityRecord> records = new Ledger().Root(Root).Vouch(Root, S).Vouch(Root, A).Name(Root, S).Rotate(S, Root).RevokeSuccessor(Root, S).Records;

        RefusedWith(
            $"The key {S.Key} cannot revoke a node: it is not a live key of the ranked root set; only a live root-set key may revoke a node.",
            () => IdentityDecider.RevokeNode(Root.Key, records, S.Key, A.Node, A.Key, Now));
    }

    [Fact]
    public void A_revocation_of_a_key_that_outranks_its_signer_or_of_the_signer_itself_is_refused()
    {
        IReadOnlyList<IdentityRecord> records = Ranked();

        RefusedWith(
            $"The key {S.Key} (rank 1) cannot revoke {Root.Key} (rank 0): that key outranks it; a root-set key may not revoke a key that outranks it, nor itself.",
            () => IdentityDecider.RevokeNode(Root.Key, records, S.Key, Root.Node, Root.Key, Now));
        RefusedWith(
            $"The key {S.Key} (rank 1) cannot revoke as a successor {Root.Key} (rank 0): that key outranks it; a root-set key may not revoke a key that outranks it, nor itself.",
            () => IdentityDecider.RevokeSuccessor(Root.Key, records, S.Key, Root.Key, Now));
        RefusedWith(
            $"The key {Root.Key} cannot revoke itself; a root-set key may not revoke a key that outranks it, nor itself.",
            () => IdentityDecider.RevokeNode(Root.Key, records, Root.Key, Root.Node, Root.Key, Now));
    }

    [Fact]
    public void A_successor_named_with_a_key_other_than_its_vouched_one_is_refused()
    {
        IReadOnlyList<IdentityRecord> records = Ranked();

        RefusedWith(
            $"Node {A.Node} was named a successor with the key {X.Key}, but a counting vouch gives it {A.Key}; a successor is named with the exact key a counting vouch gives it.",
            () => IdentityDecider.NameSuccessor(Root.Key, records, Root.Key, A.Node, X.Key, Now));
        RefusedWith(
            $"Node {X.Node} has no counting vouch, so it cannot be named a successor; a successor is a vouched node named with the exact key a counting vouch gives it.",
            () => IdentityDecider.NameSuccessor(Root.Key, records, Root.Key, X.Node, X.Key, Now));
    }

    [Fact]
    public void A_rotation_from_a_named_successor_superseding_its_namer_is_recorded()
    {
        IReadOnlyList<IdentityRecord> records = new Ledger().Root(Root).Vouch(Root, S).Name(Root, S).Records;

        Assert.Equal(new RootRotated(S.Key, Root.Key, Now), IdentityDecider.Rotate(Root.Key, records, S.Key, Root.Key, Now));
    }

    [Fact]
    public void A_rotation_from_a_key_no_live_root_key_named_is_refused()
    {
        RefusedWith(
            $"The key {A.Key} cannot rotate in: no live root key named it a successor; a rotation is signed by a successor a live root key named.",
            () => IdentityDecider.Rotate(Root.Key, Ranked(), A.Key, Root.Key, Now));

        // A successor named by a key outside the root set was named by no live root key.
        IReadOnlyList<IdentityRecord> namedByA = new Ledger().Root(Root).Vouch(Root, A).Vouch(Root, S1).Name(A, S1).Records;
        RefusedWith(
            $"The key {S1.Key} cannot rotate in: no live root key named it a successor; a rotation is signed by a successor a live root key named.",
            () => IdentityDecider.Rotate(Root.Key, namedByA, S1.Key, A.Key, Now));
    }

    [Fact]
    public void A_rotation_superseding_a_key_other_than_its_namer_is_refused()
    {
        IReadOnlyList<IdentityRecord> records = new Ledger().Root(Root).Vouch(Root, S).Vouch(Root, S1).Name(Root, S).Rotate(S, Root).Name(S, S1).Records;

        RefusedWith(
            $"The key {S1.Key} cannot supersede {Root.Key}: it was named by {S.Key}; a rotation supersedes the key that named its successor.",
            () => IdentityDecider.Rotate(Root.Key, records, S1.Key, Root.Key, Now));
        Assert.Equal(new RootRotated(S1.Key, S.Key, Now), IdentityDecider.Rotate(Root.Key, records, S1.Key, S.Key, Now));
    }

    [Fact]
    public void A_rotation_superseding_a_key_a_counting_rotation_already_supersedes_is_refused()
    {
        IReadOnlyList<IdentityRecord> records = new Ledger().Root(Root).Vouch(Root, S1).Vouch(Root, S2).Name(Root, S1).Name(Root, S2).Rotate(S1, Root).Records;

        RefusedWith(
            $"The key {Root.Key} is already superseded by a counting rotation; a key is superseded once.",
            () => IdentityDecider.Rotate(Root.Key, records, S2.Key, Root.Key, Now));
    }
}
