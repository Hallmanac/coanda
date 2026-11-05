using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using static Coanda.Tests.Core.Identity.Parties;

namespace Coanda.Tests.Core.Identity;

/// <summary>
/// The trust rule, one fixture per rule. <see cref="Settled.Of"/> feeds every fixture to the function
/// in every ordering of its records before the test reads the answer.
/// </summary>
public sealed class IdentityTrustTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static void Signs(IdentityVerdict verdict, Party party) =>
        Assert.Equal(SigningAnswer.Allowed, verdict.MaySign(party.Key, party.Node));

    private static void Refused(IdentityVerdict verdict, Party party, SigningRefusal refusal) =>
        Assert.Equal(SigningAnswer.Refused(refusal), verdict.MaySign(party.Key, party.Node));

    private static void RootSetIs(IdentityVerdict verdict, params Party[] ranked) =>
        Assert.Equal([.. ranked.Select((p, rank) => new RankedKey(p.Key, rank, p.Node))], verdict.RootSet);

    // The root

    [Fact]
    public void The_root_key_may_sign_for_the_root_node_and_ranks_0_alone()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root));

        Assert.True(verdict.HasRoot);
        Signs(verdict, Root);
        RootSetIs(verdict, Root);
        // the root key signs for its own node and no other
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.NoCountingVouch), verdict.MaySign(Root.Key, A.Node));
    }

    [Fact]
    public void Records_with_no_root_record_signed_by_the_trusted_root_key_trust_no_one()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Vouch(Root, A));

        Assert.False(verdict.HasRoot);
        Assert.Empty(verdict.RootSet);
        Refused(verdict, Root, SigningRefusal.NoRoot);
        Refused(verdict, A, SigningRefusal.NoRoot);
    }

    [Fact]
    public void A_root_record_signed_by_any_key_but_the_trusted_root_key_is_ignored()
    {
        // X records itself as the root, and records the trusted root's own pairing too.
        Ledger ledger = new Ledger()
            .Root(X)
            .Sign(X, new OwnerRootRecorded(Root.Node, Root.Key, At))
            .Vouch(X, A);
        IdentityVerdict verdict = Settled.Of(ledger);

        Assert.False(verdict.HasRoot);
        Refused(verdict, X, SigningRefusal.NoRoot);
        Refused(verdict, Root, SigningRefusal.NoRoot);
        Refused(verdict, A, SigningRefusal.NoRoot);

        // Read by a node that trusts X, the same records hold a root.
        IdentityVerdict trustingX = Settled.Of(ledger, trusted: X);
        Signs(trustingX, X);
        Signs(trustingX, A);
    }

    [Fact]
    public void Root_records_binding_the_root_key_to_two_node_ids_leave_it_able_to_sign_for_neither()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Sign(Root, new OwnerRootRecorded(A.Node, Root.Key, At), origin: A.Node).Vouch(Root, B));

        Assert.True(verdict.HasRoot);
        Assert.Empty(verdict.RootSet);
        Refused(verdict, Root, SigningRefusal.Conflicted);
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.Conflicted), verdict.MaySign(Root.Key, A.Node));
        Refused(verdict, B, SigningRefusal.VouchersMayNotSign);
    }

    // Vouches

    [Fact]
    public void A_node_vouched_by_a_vouched_node_is_trusted()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(A, B).Vouch(B, C));

        Signs(verdict, A);
        Signs(verdict, B);
        Signs(verdict, C);
        // a key signs only for the node its vouch names
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.NoCountingVouch), verdict.MaySign(B.Key, A.Node));
    }

    [Fact]
    public void A_node_vouched_only_by_an_unvouched_key_is_not_trusted()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(X, B).Vouch(B, C));

        Refused(verdict, X, SigningRefusal.NoCountingVouch);
        Refused(verdict, B, SigningRefusal.VouchersMayNotSign);
        Refused(verdict, C, SigningRefusal.VouchersMayNotSign);
    }

    [Fact]
    public void A_record_counts_only_when_its_signer_may_sign_for_the_records_own_origin()
    {
        // A's key is trusted for A's node, but this vouch arrives in B's chain.
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Sign(A, new NodeVouched(C.Node, C.Key, At), origin: B.Node));

        Signs(verdict, A);
        Refused(verdict, C, SigningRefusal.VouchersMayNotSign);
    }

    [Fact]
    public void A_vouch_of_another_key_for_the_root_nodes_id_has_no_effect()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, Root.Node, A.Key).Vouch(Root, A.Node, Root.Key));

        Signs(verdict, Root);
        RootSetIs(verdict, Root);
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.NoCountingVouch), verdict.MaySign(A.Key, Root.Node));
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.NoCountingVouch), verdict.MaySign(Root.Key, A.Node));
    }

    [Fact]
    public void Two_keys_vouched_for_one_node_id_leave_neither_able_to_sign_for_it()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(Root, A.Node, B.Key).Vouch(Root, C));

        Refused(verdict, A, SigningRefusal.Conflicted);
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.Conflicted), verdict.MaySign(B.Key, A.Node));
        Signs(verdict, C);
    }

    [Fact]
    public void One_key_vouched_for_two_node_ids_may_sign_for_neither()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(Root, B.Node, A.Key).Vouch(A, C));

        Refused(verdict, A, SigningRefusal.Conflicted);
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.Conflicted), verdict.MaySign(A.Key, B.Node));
        // what was vouched only through a conflicted pairing is not trusted either
        Refused(verdict, C, SigningRefusal.VouchersMayNotSign);
    }

    // Revocations

    [Fact]
    public void A_revocation_signed_by_an_ordinary_vouched_node_is_ignored()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(Root, B).Revoke(A, B));

        Signs(verdict, A);
        Signs(verdict, B);
    }

    [Fact]
    public void A_revoked_key_may_not_sign_whatever_order_its_vouch_and_revocation_arrive_in()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Revoke(Root, A));

        Refused(verdict, A, SigningRefusal.Revoked);
        Signs(verdict, Root);
    }

    [Fact]
    public void Every_record_a_revoked_key_signed_stops_counting_but_another_trusted_vouch_still_stands()
    {
        // A vouched B and C; the root also vouched C, then revoked A.
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(A, B).Vouch(A, C).Vouch(Root, C).Revoke(Root, A));

        Refused(verdict, A, SigningRefusal.Revoked);
        Refused(verdict, B, SigningRefusal.VouchersMayNotSign);
        Signs(verdict, C);
    }

    [Fact]
    public void Revoking_one_of_two_keys_vouched_for_one_node_id_lifts_the_conflict()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(Root, A.Node, B.Key).Revoke(Root, A.Node, B.Key));

        Signs(verdict, A);
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.Revoked), verdict.MaySign(B.Key, A.Node));
    }

    // The ranked root set

    [Fact]
    public void A_named_successor_that_rotates_ranks_one_below_the_key_it_supersedes_and_its_acts_count()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Vouch(Root, A).Name(Root, S).Rotate(S, Root).Revoke(S, A));

        RootSetIs(verdict, Root, S);
        Assert.Equal(1, verdict.RankOf(S.Key));
        Signs(verdict, S);
        // a rank-1 key's revocation counts
        Refused(verdict, A, SigningRefusal.Revoked);
    }

    [Fact]
    public void A_named_successor_without_its_rotation_has_no_rank()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Vouch(Root, A).Name(Root, S).Revoke(S, A));

        RootSetIs(verdict, Root);
        Signs(verdict, A);
    }

    [Fact]
    public void Rank_follows_the_rotation_chain_from_the_original_root()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S1).Vouch(S1, S2).Name(Root, S1).Rotate(S1, Root).Name(S1, S2).Rotate(S2, S1));

        RootSetIs(verdict, Root, S1, S2);
    }

    [Fact]
    public void A_vouch_from_outside_the_root_set_that_clashes_with_a_successor_does_not_unseat_it()
    {
        // A, an ordinary vouched node, pairs S's node with X's key and S's key with X's node.
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(Root, S).Name(Root, S).Rotate(S, Root).Vouch(A, S.Node, X.Key).Vouch(A, X.Node, S.Key));

        RootSetIs(verdict, Root, S);
        Signs(verdict, S);
        Signs(verdict, A);
    }

    [Fact]
    public void A_successor_cannot_unseat_itself_with_a_clashing_vouch()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Name(Root, S).Rotate(S, Root).Vouch(S, S.Node, X.Key).Vouch(S, X.Node, S.Key));

        RootSetIs(verdict, Root, S);
        Signs(verdict, S);
    }

    [Fact]
    public void A_successor_vouched_only_through_a_node_outside_the_root_set_does_not_count()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(A, S).Name(Root, S).Rotate(S, Root));

        RootSetIs(verdict, Root);
        Signs(verdict, S);
    }

    [Fact]
    public void A_conflict_among_the_root_sets_own_vouches_for_a_successor_still_stops_it()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Vouch(Root, S.Node, X.Key).Name(Root, S).Rotate(S, Root));

        RootSetIs(verdict, Root);
        Refused(verdict, S, SigningRefusal.Conflicted);
    }

    [Fact]
    public void A_successor_named_with_a_key_other_than_its_vouched_one_does_not_count()
    {
        // S's node is vouched with S's key; the root names it with X's key, which rotates.
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Sign(Root, new SuccessorNamed(S.Node, X.Key, At)).Sign(X, new RootRotated(X.Key, Root.Key, At), origin: S.Node));

        RootSetIs(verdict, Root);
    }

    [Fact]
    public void A_successor_named_by_a_key_outside_the_root_set_does_not_count()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(Root, S).Name(A, S).Rotate(S, A).Rotate(S, Root));

        RootSetIs(verdict, Root);
    }

    [Fact]
    public void A_rotation_that_supersedes_a_key_other_than_its_namer_does_not_count()
    {
        // The rank-1 key names S2, which rotates superseding the root rather than its namer.
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S1).Vouch(Root, S2).Name(Root, S1).Rotate(S1, Root).Name(S1, S2).Rotate(S2, Root));

        RootSetIs(verdict, Root, S1);
    }

    [Fact]
    public void Of_two_rotations_superseding_one_key_the_one_named_earlier_in_the_namers_chain_wins()
    {
        IdentityVerdict s1First = Settled.Of(new Ledger().Root(Root).Vouch(Root, S1).Vouch(Root, S2).Name(Root, S1).Name(Root, S2).Rotate(S2, Root).Rotate(S1, Root));
        RootSetIs(s1First, Root, S1);

        IdentityVerdict s2First = Settled.Of(new Ledger().Root(Root).Vouch(Root, S1).Vouch(Root, S2).Name(Root, S2).Name(Root, S1).Rotate(S2, Root).Rotate(S1, Root));
        RootSetIs(s2First, Root, S2);
    }

    [Fact]
    public void A_revocation_or_successor_revocation_of_a_key_that_outranks_its_signer_is_ignored()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Name(Root, S).Rotate(S, Root).Revoke(S, Root).RevokeSuccessor(S, Root).RevokeSuccessor(S, S));

        RootSetIs(verdict, Root, S);
        Signs(verdict, Root);
    }

    [Fact]
    public void Root_set_acts_from_a_key_outside_the_root_set_are_ignored()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, A).Vouch(Root, B).Vouch(Root, M).Revoke(A, B).Add(A, M).Name(A, B).RevokeSuccessor(A, B));

        Signs(verdict, B);
        Assert.Null(verdict.RoleOf(M.Key));
        RootSetIs(verdict, Root);
    }

    [Fact]
    public void Revoking_a_successor_voids_its_root_set_acts_but_its_node_and_its_vouches_stand()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Vouch(Root, B).Name(Root, S).Rotate(S, Root).Revoke(S, B).Vouch(S, C).RevokeSuccessor(Root, S));

        RootSetIs(verdict, Root);
        Signs(verdict, S);
        // the revocation S signed while ranked is void
        Signs(verdict, B);
        // the vouch S signed stands
        Signs(verdict, C);
    }

    [Fact]
    public void A_voided_rotation_takes_no_part_in_settling_competing_rotations()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S1).Vouch(Root, S2).Name(Root, S1).Name(Root, S2).Rotate(S1, Root).Rotate(S2, Root).RevokeSuccessor(Root, S1));

        RootSetIs(verdict, Root, S2);
        Signs(verdict, S1);
    }

    [Fact]
    public void A_successor_revoked_by_a_node_revocation_loses_its_rank_and_its_vouches()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Name(Root, S).Rotate(S, Root).Vouch(S, C).Revoke(Root, S));

        RootSetIs(verdict, Root);
        Refused(verdict, S, SigningRefusal.Revoked);
        Refused(verdict, C, SigningRefusal.VouchersMayNotSign);
    }

    [Fact]
    public void Revoking_a_ranked_successor_cuts_off_the_ranks_below_it()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S1).Vouch(S1, S2).Name(Root, S1).Rotate(S1, Root).Name(S1, S2).Rotate(S2, S1).RevokeSuccessor(Root, S1));

        RootSetIs(verdict, Root);
        Signs(verdict, S1);
        Signs(verdict, S2);
    }

    // Members

    [Fact]
    public void A_member_added_by_a_root_set_key_signs_for_its_node_and_vouches_transitively()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Add(Root, M, MemberRole.Owner).Vouch(M, A).Vouch(A, B));

        Signs(verdict, M);
        Signs(verdict, A);
        Signs(verdict, B);
        Assert.Equal(MemberRole.Owner, verdict.RoleOf(M.Key));
        // no member joins the ranked root set
        RootSetIs(verdict, Root);
    }

    [Fact]
    public void A_member_never_joins_the_ranked_root_set_even_when_named_and_rotated()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Add(Root, M).Vouch(Root, M).Name(Root, M).Rotate(M, Root));

        RootSetIs(verdict, Root);
        Signs(verdict, M);
    }

    [Fact]
    public void A_removed_member_and_what_was_trusted_only_through_it_may_not_sign()
    {
        Ledger before = new Ledger().Root(Root).Add(Root, M).Vouch(M, A).Vouch(M, C).Vouch(Root, C);
        IdentityVerdict trusted = Settled.Of(before);
        Signs(trusted, M);
        Signs(trusted, A);
        Signs(trusted, C);

        IdentityVerdict removed = Settled.Of(before.Remove(Root, M));
        Refused(removed, M, SigningRefusal.MemberRemoved);
        Refused(removed, A, SigningRefusal.VouchersMayNotSign);
        Signs(removed, C);
        Assert.Null(removed.RoleOf(M.Key));
    }

    [Fact]
    public void A_member_removal_stands_whatever_adds_are_recorded()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Name(Root, S).Rotate(S, Root).Remove(Root, M).Add(Root, M).Add(S, M));

        Refused(verdict, M, SigningRefusal.MemberRemoved);
    }

    [Fact]
    public void A_vouch_that_pairs_a_members_node_or_key_with_another_does_not_count()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Add(Root, M).Vouch(Root, M.Node, A.Key).Vouch(Root, A.Node, M.Key));

        Signs(verdict, M);
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.NoCountingVouch), verdict.MaySign(A.Key, M.Node));
        Assert.Equal(SigningAnswer.Refused(SigningRefusal.NoCountingVouch), verdict.MaySign(M.Key, A.Node));
    }

    [Fact]
    public void A_members_role_is_the_add_by_the_highest_ranked_key()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Vouch(Root, S).Name(Root, S).Rotate(S, Root).Add(S, M, MemberRole.Owner).Add(Root, M, MemberRole.Member));

        Assert.Equal(MemberRole.Member, verdict.RoleOf(M.Key));
    }

    [Fact]
    public void Among_one_keys_adds_of_a_member_the_latest_in_its_chain_gives_the_role()
    {
        IdentityVerdict verdict = Settled.Of(new Ledger().Root(Root).Add(Root, M, MemberRole.Member).Add(Root, M, MemberRole.Owner));

        Assert.Equal(MemberRole.Owner, verdict.RoleOf(M.Key));
    }
}
