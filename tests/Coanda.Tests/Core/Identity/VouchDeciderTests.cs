using Coanda.Core;
using Coanda.Core.Identity;
using Coanda.Core.Identity.Events;
using static Coanda.Tests.Core.Identity.Parties;

namespace Coanda.Tests.Core.Identity;

public sealed class VouchDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static NodeVouched? Vouch(IReadOnlyList<IdentityRecord> records, Guid node, string key) =>
        IdentityDecider.Vouch(records, Root.Node, Root.Key, node, key, Now);

    private static string Refusal(IReadOnlyList<IdentityRecord> records, Guid node, string key) =>
        Assert.Throws<DomainException>(() => Vouch(records, node, key)).Message;

    [Fact]
    public void A_vouch_is_recorded_for_a_node_and_key_the_records_bind_to_nothing_else()
    {
        Assert.Equal(new NodeVouched(A.Node, A.Key, Now), Vouch(new Ledger().Root(Root).Records, A.Node, A.Key));
        Assert.Equal(new NodeVouched(A.Node, A.Key, Now), Vouch([], A.Node, A.Key));
    }

    [Fact]
    public void A_vouch_already_held_for_the_same_node_and_key_records_nothing()
    {
        Assert.Null(Vouch(new Ledger().Root(Root).Vouch(Root, A).Records, A.Node, A.Key));
    }

    [Fact]
    public void A_vouch_for_this_nodes_own_id_is_refused()
    {
        Assert.Contains($"Node {Root.Node} is this node's own id", Refusal([], Root.Node, A.Key), StringComparison.Ordinal);
    }

    [Fact]
    public void A_vouch_for_this_nodes_own_key_is_refused()
    {
        Assert.Contains($"The key {Root.Key} is this node's own key", Refusal([], A.Node, Root.Key), StringComparison.Ordinal);
    }

    [Fact]
    public void A_vouch_for_a_node_id_already_vouched_with_another_key_is_refused()
    {
        string refusal = Refusal(new Ledger().Root(Root).Vouch(Root, A).Records, A.Node, B.Key);
        Assert.Contains($"Node {A.Node} already has a vouch in the project for the key {A.Key}, not {B.Key}", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vouch_for_a_key_already_vouched_for_another_node_id_is_refused()
    {
        string refusal = Refusal(new Ledger().Root(Root).Vouch(Root, A).Records, B.Node, A.Key);
        Assert.Contains($"The key {A.Key} already has a vouch in the project for node {A.Node}, not {B.Node}", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vouch_pairing_the_owner_roots_node_or_key_with_another_is_refused()
    {
        IReadOnlyList<IdentityRecord> records = new Ledger().Root(X).Records;
        Assert.Contains("already has the owner root", Refusal(records, X.Node, A.Key), StringComparison.Ordinal);
        Assert.Contains("already has the owner root", Refusal(records, A.Node, X.Key), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a key")]
    [InlineData("AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ")]
    [InlineData("AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEB")]
    public void A_vouch_for_a_key_not_in_the_line_formats_key_form_is_refused(string key)
    {
        Assert.Contains("is not a public key; a key is 32 bytes in standard base64 with padding", Refusal([], A.Node, key), StringComparison.Ordinal);
    }
}
