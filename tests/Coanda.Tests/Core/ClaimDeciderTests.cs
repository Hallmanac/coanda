using System.Security.Cryptography;
using System.Text;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;

namespace Coanda.Tests.Core;

/// <summary>
/// A parcel's claims: the winner is the claim whose line hash is lowest as unsigned bytes, whatever order the
/// claims are read in; a node holds the parcel only when its own claim wins and was published and its peers
/// fetched since; and the trigger loop starts on an unclaimed parcel only for this node's own event.
/// </summary>
public sealed class ClaimDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Self = Guid.Parse("01a1ad07-0000-7000-8000-000000000001");
    private static readonly Guid Peer = Guid.Parse("01a1ad07-0000-7000-8000-000000000002");

    [Fact]
    public void The_winner_is_the_lowest_line_hash_as_unsigned_bytes_for_every_order_of_the_claims()
    {
        // A high first byte would sort first as signed bytes, and a hex string's case would sort differently too.
        StoredClaim high = Claim(Ids.New(), "80" + new string('0', 62));
        StoredClaim low = Claim(Ids.New(), "7f" + new string('f', 62));
        StoredClaim[] others = [.. Enumerable.Range(0, 6).Select(i => Claim(Ids.New(), HashOf($"line {i}")))];
        StoredClaim[] all = [high, low, .. others];
        StoredClaim expected = all.MinBy(c => Convert.FromHexString(c.LineHash?["sha256:".Length..] ?? string.Empty), ByteOrder.Instance)
            ?? throw new InvalidOperationException("No claims.");

        var random = new Random(20261105);
        for (int shuffle = 0; shuffle < 200; shuffle++)
        {
            StoredClaim[] shuffled = [.. all];
            random.Shuffle(shuffled);
            Assert.Equal(expected, ClaimDecider.Winner(shuffled));
        }

        Assert.Equal(low, ClaimDecider.Winner([high, low]));
        Assert.Equal(low, ClaimDecider.Winner([low, high]));
        Assert.Null(ClaimDecider.Winner([]));
    }

    [Fact]
    public void A_parcel_with_no_claim_is_unclaimed_and_a_claim_on_no_line_yet_is_not_held()
    {
        Assert.IsType<ClaimStanding.Unclaimed>(ClaimDecider.Standing([]));
        Assert.IsType<ClaimStanding.NotYetHeld>(ClaimDecider.Standing([new StoredClaim(Self, null, IsOwn: true, IsPublished: false)]));

        // Another node's claim may be lower than this node's, which has no hash yet, so no winner is named.
        Assert.IsType<ClaimStanding.NotYetHeld>(ClaimDecider.Standing(
            [new StoredClaim(Self, null, IsOwn: true, IsPublished: false), Claim(Peer, "00" + new string('0', 62))]));
    }

    [Fact]
    public void This_node_holds_the_parcel_only_when_its_own_published_claim_wins()
    {
        StoredClaim mineLow = new(Self, "sha256:00" + new string('0', 62), IsOwn: true, IsPublished: true);
        StoredClaim theirsHigh = Claim(Peer, "ff" + new string('f', 62));
        Assert.IsType<ClaimStanding.Held>(ClaimDecider.Standing([theirsHigh, mineLow]));
        Assert.IsType<ClaimStanding.NotYetHeld>(ClaimDecider.Standing([theirsHigh, mineLow with { IsPublished = false }]));

        StoredClaim mineHigh = mineLow with { LineHash = "sha256:ff" + new string('f', 62) };
        StoredClaim theirsLow = Claim(Peer, "00" + new string('0', 62));
        Assert.Equal(new ClaimStanding.Lost(Peer), ClaimDecider.Standing([mineHigh, theirsLow]));
        Assert.Equal(new ClaimStanding.Lost(Peer), ClaimDecider.Standing([theirsLow]));
    }

    [Fact]
    public void A_claim_is_refused_on_a_closed_parcel_and_after_another_nodes_claim_and_made_again_it_records_nothing()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now));

        DateTimeOffset later = Now.AddMinutes(5);
        ParcelClaimed claimed = ClaimDecider.Claim(parcel, [], Self, "brian", later) ?? throw new InvalidOperationException("The claim was not recorded.");
        Assert.Equal((parcel.Id, Self, "brian", later), (claimed.ParcelId, claimed.NodeId, claimed.ClaimedBy, claimed.ClaimedAt));
        parcel.Apply(claimed);
        Assert.Equal([Self], parcel.Claimants);
        Assert.Equal(later, parcel.UpdatedAt);

        StoredClaim mine = new(Self, null, IsOwn: true, IsPublished: false);
        StoredClaim theirs = Claim(Peer, HashOf("theirs"));
        Assert.Null(ClaimDecider.Claim(parcel, [mine], Self, "brian", Now));
        Assert.Null(ClaimDecider.Claim(parcel, [theirs, mine], Self, "brian", Now));

        DomainException seen = Assert.Throws<DomainException>(() => ClaimDecider.Claim(parcel, [theirs], Self, "brian", Now));
        Assert.Contains($"already claimed by node {Peer}", seen.Message, StringComparison.Ordinal);

        parcel.Apply(new ParcelClosed(parcel.Id, parcel.Stage, "closed", "abandoned", "Not needed.", "brian", Now));
        DomainException closed = Assert.Throws<DomainException>(() => ClaimDecider.Claim(parcel, [], Self, "brian", Now));
        Assert.Contains("a closed parcel is claimed by nobody", closed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_trigger_loop_starts_on_a_held_parcel_for_any_event_on_an_unclaimed_one_only_for_its_own_and_otherwise_never()
    {
        ClaimStanding[] standings = [new ClaimStanding.Held(), new ClaimStanding.Unclaimed(), new ClaimStanding.NotYetHeld("why"), new ClaimStanding.Lost(Peer)];
        Assert.Equal(
            [(true, true), (true, false), (false, false), (false, false)],
            standings.Select(s => (ClaimDecider.Triggers(s, ownEvent: true), ClaimDecider.Triggers(s, ownEvent: false))));
    }

    [Fact]
    public void A_start_on_a_claimed_parcel_not_held_is_refused_naming_the_winner_or_why_this_nodes_claim_is_not_held()
    {
        Guid parcel = Ids.New();
        ClaimDecider.RequireMayStart(parcel, new ClaimStanding.Held());
        ClaimDecider.RequireMayStart(parcel, new ClaimStanding.Unclaimed());

        DomainException lost = Assert.Throws<DomainException>(() => ClaimDecider.RequireMayStart(parcel, new ClaimStanding.Lost(Peer)));
        Assert.Contains($"node {Peer}'s claim wins it", lost.Message, StringComparison.Ordinal);
        DomainException pending = Assert.Throws<DomainException>(() => ClaimDecider.RequireMayStart(parcel, new ClaimStanding.NotYetHeld("no push yet")));
        Assert.Contains("this node's own claim on it is not yet held: no push yet", pending.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parcels_live_flow_run_is_the_first_attached_while_none_was_live_and_only_its_own_release_clears_it()
    {
        Parcel parcel = Parcel.Create(ParcelDecider.Create(SoftwareTypes.Task, GateSet.Shipped, "coanda", "x", null, "brian", Now));
        Guid mine = Ids.New();
        Guid theirs = Ids.New();

        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, mine, Now));
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, theirs, Now));
        parcel.Apply(new ParcelFlowRunReleased(parcel.Id, theirs, Now));
        Assert.Equal(mine, parcel.LiveFlowRunId);

        parcel.Apply(new ParcelFlowRunReleased(parcel.Id, mine, Now));
        Assert.Null(parcel.LiveFlowRunId);
        parcel.Apply(new ParcelFlowRunAttached(parcel.Id, theirs, Now));
        Assert.Equal(theirs, parcel.LiveFlowRunId);
    }

    private static StoredClaim Claim(Guid node, string hex) =>
        new(node, hex.StartsWith("sha256:", StringComparison.Ordinal) ? hex : $"sha256:{hex}", IsOwn: false, IsPublished: false);

    private static string HashOf(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
