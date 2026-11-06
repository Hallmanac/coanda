using Coanda.Core;
using Coanda.Core.Identity.Events;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Identity;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// <c>coa node vouch</c>'s side of a node: a vouch recorded in the project's tenant and pushed before it answers,
/// the owner root recorded with the first vouch on the owner's node, and every case it refuses, recording nothing.
/// </summary>
public sealed class VouchTests
{
    private const string Alpha = "alpha";
    private static readonly CancellationToken None = LedgerNode.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    [Fact]
    public async Task The_first_vouch_records_the_owner_root_and_the_trusted_root_and_pushes_both_with_the_vouch()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();

        NodeVouchResult vouched = await node.Voucher.VouchAsync(Owner, Alpha, other.NodeId, other.Key.PublicKey, None);

        Assert.Equal((other.NodeId, other.Key.PublicKey, true, true, true, (string?)null), (vouched.NodeId, vouched.Key, vouched.Recorded, vouched.RootRecorded, vouched.Pushed, vouched.Reason));
        IReadOnlyList<IEvent> events = await node.EventsAsync(Alpha);
        OwnerRootRecorded root = Assert.Single(events.Select(e => e.Data).OfType<OwnerRootRecorded>());
        Assert.Equal((node.Identity.NodeId, node.Identity.Key.PublicKey), (root.NodeId, root.Key));
        NodeVouched vouch = Assert.Single(events.Select(e => e.Data).OfType<NodeVouched>());
        Assert.Equal((other.NodeId, other.Key.PublicKey), (vouch.NodeId, vouch.Key));
        Assert.Equal(node.Identity.Key.PublicKey, (await RootOfAsync(node))?.Key);

        // Both are on lines in the remote's copy of this node's ref.
        IReadOnlyList<SignedLine> pushed = await node.ParsedAsync(node.RemotePath(Alpha));
        Assert.Contains(pushed, l => l.Content.Event.Type == "owner_root_recorded");
        Assert.Contains(pushed, l => l.Content.Event.Type == "node_vouched");

        // The node still records in the project as its owner.
        await new ParcelService(node.Store, TimeProvider.System).AddAsync(Owner, Alpha, "task", "After the vouch", null, None);
        Assert.Single(await new ParcelService(node.Store, TimeProvider.System).ListAsync(Owner, Alpha, None));
    }

    [Fact]
    public async Task A_vouch_whose_push_fails_says_it_is_recorded_and_not_yet_pushed_and_a_vouch_made_again_pushes_it()
    {
        await using LedgerNode node = await LedgerNode.StartAsync();
        await node.AddProjectAsync(Alpha, withRemote: false);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();

        NodeVouchResult first = await node.Voucher.VouchAsync(Owner, Alpha, other.NodeId, other.Key.PublicKey, None);
        Assert.Equal((true, true, false), (first.Recorded, first.RootRecorded, first.Pushed));
        Assert.False(string.IsNullOrWhiteSpace(first.Reason));
        int events = (await node.EventsAsync(Alpha)).Count;

        await node.MakeRemoteAsync(Alpha);
        NodeVouchResult again = await node.Voucher.VouchAsync(Owner, Alpha, other.NodeId, other.Key.PublicKey, None);

        Assert.Equal((false, false, true, (string?)null), (again.Recorded, again.RootRecorded, again.Pushed, again.Reason));
        Assert.Equal(events, (await node.EventsAsync(Alpha)).Count);
        Assert.Contains(await node.ParsedAsync(node.RemotePath(Alpha)), l => l.Content.Event.Type == "node_vouched");
    }

    [Fact]
    public async Task A_vouch_for_this_nodes_own_id_is_refused_and_records_nothing()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();

        await RefusedAsync(node, node.Identity.NodeId, other.Key.PublicKey, "is this node's own id");
    }

    [Fact]
    public async Task A_vouch_for_this_nodes_own_key_is_refused_and_records_nothing()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();

        await RefusedAsync(node, other.NodeId, node.Identity.Key.PublicKey, "is this node's own key");
    }

    [Fact]
    public async Task A_vouch_for_a_node_id_vouched_with_another_key_is_refused_and_records_nothing()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();
        NodeIdentity third = await LedgerNode.OtherIdentityAsync();
        await node.Voucher.VouchAsync(Owner, Alpha, other.NodeId, other.Key.PublicKey, None);

        await RefusedAsync(node, other.NodeId, third.Key.PublicKey, $"already has a vouch in the project for the key {other.Key.PublicKey}");
    }

    [Fact]
    public async Task A_vouch_for_a_key_vouched_for_another_node_id_is_refused_and_records_nothing()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();
        NodeIdentity third = await LedgerNode.OtherIdentityAsync();
        await node.Voucher.VouchAsync(Owner, Alpha, other.NodeId, other.Key.PublicKey, None);

        await RefusedAsync(node, third.NodeId, other.Key.PublicKey, $"already has a vouch in the project for node {other.NodeId}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("not base64 at all")]
    [InlineData("AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE")]
    public async Task A_vouch_for_a_key_that_is_not_32_bytes_in_the_line_formats_key_form_is_refused_and_records_nothing(string key)
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();

        await RefusedAsync(node, other.NodeId, key, "is not a public key; a key is 32 bytes in standard base64 with padding");
    }

    [Theory]
    [InlineData(Role.Member)]
    [InlineData(Role.Agent)]
    [InlineData(Role.Module)]
    public async Task A_vouch_is_open_to_the_owner_role_only(Role role)
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();

        await RefusedAsync(node, other.NodeId, other.Key.PublicKey, $"'node.vouch' is not open to the {role} role", new Principal("someone", role));
    }

    [Fact]
    public async Task A_node_id_that_is_not_a_hyphenated_uuid_is_refused_after_the_role_check()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();

        DomainException member = await Assert.ThrowsAsync<DomainException>(
            () => node.Voucher.VouchAsync(new Principal("someone", Role.Member), Alpha, "not-an-id", other.Key.PublicKey, None));
        Assert.Contains("'node.vouch' is not open to the Member role", member.Message, StringComparison.Ordinal);

        DomainException owner = await Assert.ThrowsAsync<DomainException>(() => node.Voucher.VouchAsync(Owner, Alpha, "not-an-id", other.Key.PublicKey, None));
        Assert.Contains("'not-an-id' is not a node id", owner.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await node.EventsAsync(Alpha), e => e.Data is NodeVouched);
    }

    [Fact]
    public async Task A_vouch_in_a_project_whose_registration_was_imported_and_which_holds_no_root_is_refused()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode peer = await LedgerNode.StartAsync();
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();

        // The peer holds the owner's registration, stored from the owner's line, and no root record.
        await PublishAsync(owner);
        await StoreFirstLineAsync(owner, peer);
        int events = (await peer.EventsAsync(Alpha)).Count;

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => peer.Voucher.VouchAsync(Owner, Alpha, other.NodeId, other.Key.PublicKey, None));

        Assert.Contains("the owner's node records the root with its first vouch", refused.Message, StringComparison.Ordinal);
        Assert.Equal(events, (await peer.EventsAsync(Alpha)).Count);
        Assert.Null(await RootOfAsync(peer));
    }

    [Fact]
    public async Task A_project_in_which_nothing_was_vouched_holds_no_identity_event_and_no_root_key_record()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        await new ParcelService(node.Store, TimeProvider.System).AddAsync(Owner, Alpha, "task", "Unvouched", null, None);
        await PublishAsync(node);

        Assert.DoesNotContain(await node.EventsAsync(Alpha), e => e.Data is OwnerRootRecorded or NodeVouched);
        Assert.Null(await RootOfAsync(node));
    }

    /// <summary>
    /// Stores the owner's first line, its registration, in <paramref name="peer"/>'s store as the import does, as a
    /// joined node whose import stopped after the registration line holds it.
    /// </summary>
    internal static async Task StoreFirstLineAsync(LedgerNode owner, LedgerNode peer)
    {
        SignedLine first = (await owner.ParsedAsync(owner.RemotePath(Alpha)))[0];
        Assert.Equal("project_registered", first.Content.Event.Type);
        ProjectRegistered registered = Assert.IsType<ProjectRegistered>(peer.Store.Options.Serializer.FromJson(typeof(ProjectRegistered), first.Content.Event.Data));
        await using IDocumentSession session = peer.Store.LightweightSession(Alpha);
        IEvent appended = session.Events.Append(first.Content.StreamId, registered).Events[^1];
        appended.Timestamp = first.Content.RecordedAt;
        session.Store(new LedgerLine
        {
            Id = appended.Id,
            Origin = first.Content.Origin,
            Sequence = first.Content.Sequence,
            PreviousHash = first.Content.PreviousHash,
            Tag = first.Content.Tag.RepoNames,
            Signature = first.Signature,
            Hash = first.Hash,
            Segment = (await owner.RecordsAsync(Alpha))[0].Segment,
            Line = System.Text.Encoding.UTF8.GetString(first.Bytes),
        });
        await session.SaveChangesAsync(None);
    }

    private static async Task RefusedAsync(LedgerNode node, Guid nodeId, string key, string expected, Principal? principal = null)
    {
        int events = (await node.EventsAsync(Alpha)).Count;
        LedgerRoot? root = await RootOfAsync(node);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => node.Voucher.VouchAsync(principal ?? Owner, Alpha, nodeId, key, None));

        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.Equal(events, (await node.EventsAsync(Alpha)).Count);
        Assert.Equal(root?.Key, (await RootOfAsync(node))?.Key);
    }

    private static async Task<LedgerRoot?> RootOfAsync(LedgerNode node)
    {
        await using IQuerySession session = node.Store.QuerySession(Alpha);
        return await session.LoadAsync<LedgerRoot>(LedgerRoot.DocumentId, None);
    }

    private static async Task PublishAsync(LedgerNode node)
    {
        LedgerPublication published = await node.Export.PublishAsync(Alpha, None);
        Assert.True(published.Published, string.Join("; ", published.Failures));
    }
}
