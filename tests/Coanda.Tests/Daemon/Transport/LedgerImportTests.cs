using System.Text;
using System.Text.RegularExpressions;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Identity.Events;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Projects;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Signing;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Commands;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// The node's import: every clone fetches its peers' ledger refs from a local bare repository standing in for
/// the remote, and each peer's lines are stored once, by origin and sequence, only when the chain verifies, the
/// signer may sign for its origin, and the event is in this build's current shape.
/// </summary>
public sealed class LedgerImportTests
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private static readonly CancellationToken None = LedgerNode.None;
    private static readonly DateTimeOffset At = PeerChain.Recorded;

    [Fact]
    public async Task A_round_fetches_only_the_peers_ledger_refs_and_stores_each_line_once_with_its_record()
    {
        await using Peers peers = await Peers.StartAsync();
        PeerChain peer = peers.Vouched();
        byte[] first = peer.Add(Created(out Guid parcel), parcel);
        byte[] second = peer.Add(new ParcelMoved(parcel, "inbox", "build", "peer", At), parcel);
        await peers.PushAsync(peers.Root, peer);

        // The remote holds a branch, a tag, and this node's own ref besides; the clone holds this node's own ref too.
        string remote = peers.Remote;
        string commit = (await LedgerNode.GitAsync(remote, "rev-parse", peer.RefName)).Trim();
        await LedgerNode.GitAsync(remote, "update-ref", "refs/heads/main", commit);
        await LedgerNode.GitAsync(remote, "update-ref", "refs/tags/v1", commit);
        await LedgerNode.GitAsync(remote, "update-ref", peers.Node.RefName, commit);
        string clone = peers.Node.ClonePath(Alpha);
        await LedgerNode.GitAsync(clone, "fetch", "--quiet", "--no-tags", remote, $"{peer.RefName}:{peers.Node.RefName}");

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        Guid self = peers.Node.Identity.NodeId;
        Assert.Equal(
            [$"fetch --no-tags --no-write-fetch-head origin +refs/coanda/nodes/*:refs/coanda/fetched/nodes/* ^refs/coanda/nodes/{self:D}"],
            peers.Node.ImportGit.Calls.Where(c => c.StartsWith("fetch", StringComparison.Ordinal)));
        Assert.Equal(
            [.. new[] { $"{RepoClones.LedgerNamespace}{peers.Root.Origin:D}", $"{RepoClones.LedgerNamespace}{peer.Origin:D}", peers.Node.RefName }.Order(StringComparer.Ordinal)],
            (await LedgerNode.GitAsync(clone, "for-each-ref", "--format=%(refname)")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Equal(commit, await peers.Node.RefAsync(clone));

        Assert.Empty(report.Failures);
        Assert.Equal(
            [.. new[] { new OriginImport(peers.Root.Origin, 2, 2, null), new OriginImport(peer.Origin, 2, 2, null) }.OrderBy(o => o.Origin)],
            report.Origins);

        // Each event is appended to the stream its line names, under a local event id, with the line's
        // recorded time and its record beside it.
        IReadOnlyList<LedgerLine> records = await peers.Node.RecordsAsync(Alpha);
        IReadOnlyList<IEvent> events = [.. (await peers.Node.EventsAsync(Alpha)).Where(e => records.Any(r => r.Id == e.Id))];
        Assert.Equal((4, 4), (events.Count, records.Count));
        IEvent[] moved = [.. events.Where(e => e.StreamId == parcel)];
        Assert.Equal(["parcel_created", "parcel_moved"], moved.Select(e => e.EventTypeName));
        Assert.All(moved, e => Assert.Equal(At, e.Timestamp));
        foreach ((IEvent entry, byte[] line) in moved.Zip([first, second]))
        {
            SignedLine signed = SignedLine.Parse(line, out _) ?? throw new InvalidOperationException("The line parses.");
            LedgerLine record = Assert.Single(records, r => r.Id == entry.Id);
            Assert.NotEqual(signed.Content.EventId, entry.Id);
            Assert.Equal(
                (peer.Origin, signed.Content.Sequence, signed.Content.PreviousHash, signed.Signature, signed.Hash, Encoding.UTF8.GetString(line), "2026-11"),
                (record.Origin, record.Sequence, record.PreviousHash, record.Signature, record.Hash, record.Line, record.Segment));
        }

        // The same lines a second time store nothing.
        LedgerImportReport again = await peers.Node.Import.ImportAsync(Alpha, None);
        Assert.All(again.Origins, o => Assert.Equal(0, o.Stored));
        Assert.Equal(4, (await peers.Node.RecordsAsync(Alpha)).Count);
        Assert.Equal(5, (await peers.Node.EventsAsync(Alpha)).Count);
    }

    [Fact]
    public async Task An_origins_ref_through_two_clones_and_two_imports_at_once_store_each_line_once()
    {
        await using LedgerNode node = await LedgerNode.StartAsync();
        string one = Path.Combine(node.Home.Path, "remotes", Alpha, "one.git");
        string two = Path.Combine(node.Home.Path, "remotes", Alpha, "two.git");
        Guid id = Ids.New();
        await node.AddProjectAsync(Alpha, id, [one, two]);
        var peers = new Peers(node, id, one);
        await node.TrustRootAsync(Alpha, peers.Root.Key.PublicKey);
        PeerChain peer = peers.Vouched();
        peer.Add(Created(out Guid parcel), parcel);
        foreach (string remote in new[] { one, two })
        {
            await peers.Root.PushAsync(remote);
            await peer.PushAsync(remote);
        }

        LedgerImportReport[] reports = await Task.WhenAll(node.Import.ImportAsync(Alpha, None), node.Import.ImportAsync(None).ContinueWith(t => t.Result.Single(), TaskScheduler.Default));

        Assert.Equal(3, reports.SelectMany(r => r.Origins).Sum(o => o.Stored));
        Assert.Equal(1 + 3, (await node.EventsAsync(Alpha)).Count);
        Assert.Equal(
            [(peers.Root.Origin, 1L), (peers.Root.Origin, 2L), (peer.Origin, 1L)],
            [.. (await node.RecordsAsync(Alpha)).Select(r => (r.Origin, r.Sequence)).OrderBy(r => r.Item1 == peer.Origin).ThenBy(r => r.Item2)]);
    }

    [Fact]
    public async Task A_failed_fetch_is_reported_and_tried_again_while_the_other_clones_and_projects_import()
    {
        await using LedgerNode node = await LedgerNode.StartAsync();
        string held = Path.Combine(node.Home.Path, "remotes", Alpha, "held.git");
        string gone = Path.Combine(node.Home.Path, "remotes", Alpha, "gone.git");
        Guid alphaId = Ids.New();
        await node.AddProjectAsync(Alpha, alphaId, [held, gone]);
        Directory.Delete(gone, recursive: true);
        var alpha = new Peers(node, alphaId, held);
        await node.TrustRootAsync(Alpha, alpha.Root.Key.PublicKey);
        await alpha.Root.PushAsync(held);

        Guid betaId = Ids.New();
        string betaRemote = Path.Combine(node.Home.Path, "remotes", Beta, "repo.git");
        await node.AddProjectAsync(Beta, betaId, [betaRemote]);
        var beta = new Peers(node, betaId, betaRemote);
        await node.TrustRootAsync(Beta, beta.Root.Key.PublicKey);
        await beta.Root.PushAsync(betaRemote);

        IReadOnlyList<LedgerImportReport> round = await node.Import.ImportAsync(None);

        LedgerImportReport alphaReport = Assert.Single(round, r => r.Project == Alpha);
        string failure = Assert.Single(alphaReport.Failures);
        Assert.Contains("repo 'gone'", failure, StringComparison.Ordinal);
        Assert.Contains("fetch", failure, StringComparison.Ordinal);
        Assert.Contains(node.Logs.Entries, e => e.Message.Contains(failure, StringComparison.Ordinal));
        Assert.Equal([new OriginImport(alpha.Root.Origin, 1, 1, null)], alphaReport.Origins);
        LedgerImportReport betaReport = Assert.Single(round, r => r.Project == Beta);
        Assert.Empty(betaReport.Failures);
        Assert.Equal([new OriginImport(beta.Root.Origin, 1, 1, null)], betaReport.Origins);

        // The next round fetches again, and the remote that is back is fetched.
        Directory.CreateDirectory(gone);
        await LedgerNode.GitAsync(gone, "init", "--bare", "--quiet");
        Assert.Empty((await node.Import.ImportAsync(Alpha, None)).Failures);
    }

    [Fact]
    public async Task A_changed_line_stops_its_origin_while_another_origin_imports_in_the_same_round()
    {
        await using Peers peers = await Peers.StartAsync();
        (PeerChain faulty, PeerChain sound) = (peers.Vouched(), peers.Vouched());
        Fill(faulty, 3);
        Fill(sound, 2);
        await peers.PushAsync(peers.Root, sound);
        await faulty.PushAsync(peers.Remote, [faulty.Lines[0], Lines.Replace(faulty.Lines[1], "\"title\":\"Parcel 2\"", "\"title\":\"Changed\""), faulty.Lines[2]]);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, faulty, ImportFaultKind.BadSignature, at: 2, stored: 1);
        Assert.Equal(new OriginImport(sound.Origin, 2, 2, null), Of(report, sound));
        Assert.Contains(peers.Node.Logs.Entries, e => e.Message.Contains($"origin {faulty.Origin}", StringComparison.Ordinal) && e.Message.Contains("line 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_gap_stops_its_origin_at_the_line_before_it_while_another_origin_imports()
    {
        await using Peers peers = await Peers.StartAsync();
        (PeerChain faulty, PeerChain sound) = (peers.Vouched(), peers.Vouched());
        Fill(faulty, 4);
        Fill(sound, 1);
        await peers.PushAsync(peers.Root, sound);
        await faulty.PushAsync(peers.Remote, [faulty.Lines[0], faulty.Lines[1], faulty.Lines[3]]);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, faulty, ImportFaultKind.WrongSequence, at: 4, stored: 2, held: 2);
        Assert.Equal(new OriginImport(sound.Origin, 1, 1, null), Of(report, sound));

        // The next round tries again from the line held, and stores the rest once the gap is filled.
        await faulty.PushAsync(peers.Remote);
        Assert.Equal(new OriginImport(faulty.Origin, 2, 4, null), Of(await peers.Node.Import.ImportAsync(Alpha, None), faulty));
    }

    [Fact]
    public async Task A_rewind_behind_the_held_line_stops_its_origin_while_another_origin_imports()
    {
        await using Peers peers = await Peers.StartAsync();
        (PeerChain faulty, PeerChain sound) = (peers.Vouched(), peers.Vouched());
        Fill(faulty, 3);
        Fill(sound, 1);
        await peers.PushAsync(peers.Root, faulty, sound);
        await peers.Node.Import.ImportAsync(Alpha, None);

        await faulty.PushAsync(peers.Remote, faulty.Lines.Take(2));
        Fill(sound, 1);
        await sound.PushAsync(peers.Remote);
        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, faulty, ImportFaultKind.Rewind, at: 2, stored: 0, held: 3);
        Assert.Equal(new OriginImport(sound.Origin, 1, 2, null), Of(report, sound));
    }

    [Fact]
    public async Task A_ref_whose_lines_moved_to_an_earlier_segment_file_is_read_whole_and_not_called_rewound()
    {
        await using Peers peers = await Peers.StartAsync();
        PeerChain peer = peers.Vouched();
        Fill(peer, 2);
        await peers.PushAsync(peers.Root, peer);
        await peers.Node.Import.ImportAsync(Alpha, None);

        Fill(peer, 1);
        await peer.PushAsync(peers.Remote, segment: "2026-10");

        Assert.Equal(new OriginImport(peer.Origin, 1, 3, null), Of(await peers.Node.Import.ImportAsync(Alpha, None), peer));
    }

    [Fact]
    public async Task A_different_line_at_the_held_sequence_stops_its_origin_while_another_origin_imports()
    {
        await using Peers peers = await Peers.StartAsync();
        (PeerChain faulty, PeerChain sound) = (peers.Vouched(), peers.Vouched());
        Fill(faulty, 2);
        await peers.PushAsync(peers.Root, faulty);
        await peers.Node.Import.ImportAsync(Alpha, None);

        PeerChain fork = faulty.Fork(1);
        Fill(fork, 2);
        Fill(sound, 1);
        await peers.PushAsync(fork, sound);
        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, faulty, ImportFaultKind.HeldHashMismatch, at: 2, stored: 0, held: 2);
        Assert.Equal(new OriginImport(sound.Origin, 1, 1, null), Of(report, sound));
        Assert.Equal(
            faulty.Lines.Select(Encoding.UTF8.GetString),
            (await peers.Node.RecordsAsync(Alpha)).Where(r => r.Origin == faulty.Origin).Select(r => r.Line));
    }

    [Fact]
    public async Task A_line_signed_by_a_key_that_may_not_sign_for_its_origin_stops_it_while_another_origin_imports()
    {
        await using Peers peers = await Peers.StartAsync();
        (PeerChain faulty, PeerChain sound) = (peers.Vouched(), peers.Vouched());
        Fill(faulty, 1);
        faulty.Add(Created(out Guid parcel), parcel, signer: Ed25519Key.Generate());
        Fill(sound, 1);
        PeerChain stranger = PeerChain.Of(peers.Node.Store, peers.ProjectId);
        Fill(stranger, 1);
        await peers.PushAsync(peers.Root, faulty, sound, stranger);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, faulty, ImportFaultKind.UntrustedSigner, at: 2, stored: 1);
        AssertStopped(report, stranger, ImportFaultKind.UntrustedSigner, at: 1, stored: 0);
        Assert.Equal(new OriginImport(sound.Origin, 1, 1, null), Of(report, sound));
    }

    [Fact]
    public async Task An_identity_event_in_a_line_not_stored_withdraws_no_trust_from_the_lines_before_it()
    {
        await using Peers peers = await Peers.StartAsync();
        PeerChain peer = peers.Vouched();
        Fill(peer, 1);
        // A vouch pairing another node id with the origin's own key leaves the key signing for neither.
        peer.Add(new NodeVouched(Ids.New(), peer.Key.PublicKey, At));
        Fill(peer, 1);
        await peers.PushAsync(peers.Root, peer);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, peer, ImportFaultKind.UntrustedSigner, at: 2, stored: 1);
        Assert.Contains("Line 2 is signed by", Of(report, peer).Fault?.Reason, StringComparison.Ordinal);
        Assert.Equal([1L], (await peers.Node.RecordsAsync(Alpha)).Where(r => r.Origin == peer.Origin).Select(r => r.Sequence));
    }

    [Fact]
    public async Task A_line_holding_a_member_this_build_does_not_know_stops_its_origin()
    {
        await using Peers peers = await Peers.StartAsync();
        PeerChain peer = peers.Vouched();
        Fill(peer, 1);
        string current = peer.Stored(Created(out Guid parcel)).Data;
        peer.Add(new StoredEvent("parcel_created", "{\"addedLater\":1," + current[1..]), parcel);
        await peers.PushAsync(peers.Root, peer);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, peer, ImportFaultKind.LaterShape, at: 2, stored: 1);
    }

    [Fact]
    public async Task A_line_the_store_refuses_stops_its_origin_with_the_stores_reason()
    {
        await using Peers peers = await Peers.StartAsync();
        Guid archived = Ids.New();
        await using (IDocumentSession session = peers.Node.Store.LightweightSession(Alpha))
        {
            session.Events.StartStream(archived, new ParcelCreated(archived, Alpha, "task", "sha256:type", "inbox", "Archived", "owner", At));
            await session.SaveChangesAsync(None);
        }

        await using (IDocumentSession session = peers.Node.Store.LightweightSession(Alpha))
        {
            session.Events.ArchiveStream(archived);
            await session.SaveChangesAsync(None);
        }

        (PeerChain faulty, PeerChain sound) = (peers.Vouched(), peers.Vouched());
        Fill(faulty, 1);
        faulty.Add(new ParcelMoved(archived, "inbox", "build", "peer", At), archived);
        Fill(faulty, 1);
        Fill(sound, 1);
        await peers.PushAsync(peers.Root, faulty, sound);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, faulty, ImportFaultKind.StoreRefused, at: 2, stored: 1);
        Assert.Contains("the store refused", Of(report, faulty).Fault?.Reason, StringComparison.Ordinal);
        Assert.Equal(new OriginImport(sound.Origin, 1, 1, null), Of(report, sound));
    }

    [Fact]
    public async Task A_line_naming_another_origin_or_project_is_a_fault_and_this_nodes_own_ref_is_never_read()
    {
        await using Peers peers = await Peers.StartAsync();
        LedgerNode node = peers.Node;
        (PeerChain naming, PeerChain elsewhere) = (peers.Vouched(), peers.Vouched());
        Fill(naming, 1);
        naming.Add(naming.Stored(Created(out Guid one)), one, lineOrigin: node.Identity.NodeId);
        elsewhere.Add(elsewhere.Stored(Created(out Guid two)), two, lineProject: Ids.New());

        // This node's own ref, on the remote and among the clone's fetched refs, holding a line naming this node.
        var own = new PeerChain(node.Store, peers.ProjectId, node.Identity.NodeId, node.Identity.Key);
        Fill(own, 1);
        await peers.PushAsync(peers.Root, naming, elsewhere, own);
        await LedgerNode.GitAsync(node.ClonePath(Alpha), "fetch", "--quiet", "--no-tags", peers.Remote, $"{own.RefName}:{RepoClones.LedgerNamespace}{node.Identity.NodeId:D}");

        LedgerImportReport report = await node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, naming, ImportFaultKind.WrongOrigin, at: 2, stored: 1);
        AssertStopped(report, elsewhere, ImportFaultKind.WrongProject, at: 1, stored: 0);
        Assert.DoesNotContain(report.Origins, o => o.Origin == node.Identity.NodeId);
        Assert.DoesNotContain(await node.RecordsAsync(Alpha), r => r.Origin == node.Identity.NodeId);
    }

    [Fact]
    public async Task A_project_with_no_root_key_record_imports_no_line_from_another_origin_and_says_why()
    {
        await using LedgerNode node = await LedgerNode.StartAsync();
        Guid id = Ids.New();
        await node.AddProjectAsync(Alpha, id, [node.RemotePath(Alpha)]);
        var peers = new Peers(node, id, node.RemotePath(Alpha));
        PeerChain peer = peers.Vouched();
        Fill(peer, 1);
        await peers.PushAsync(peers.Root, peer);

        LedgerImportReport report = await node.Import.ImportAsync(Alpha, None);

        Assert.Equal(2, report.Origins.Count);
        Assert.All(report.Origins, o => Assert.Equal((0, (long?)null, ImportFaultKind.NoTrustedRoot), (o.Stored, o.Held, o.Fault?.Kind)));
        Assert.Contains("no root key record", report.Origins[0].Fault?.Reason, StringComparison.Ordinal);
        Assert.Empty(await node.RecordsAsync(Alpha));

        // A second round says the same in its report and logs nothing more: the one warning stands for every origin and round.
        LedgerImportReport again = await node.Import.ImportAsync(Alpha, None);

        Assert.All(again.Origins, o => Assert.Equal(ImportFaultKind.NoTrustedRoot, o.Fault?.Kind));
        Assert.Single(node.Logs.Entries, e => e.Message.Contains("no root key record", StringComparison.Ordinal));
        Assert.DoesNotContain(node.Logs.Entries, e => e.Message.Contains("stopped at", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("00000000-0000-7000-8000-000000000001")]
    [InlineData("ffffffff-ffff-7fff-bfff-ffffffffffff")]
    public async Task An_origin_vouched_for_only_in_another_origins_lines_of_the_same_round_imports_whichever_ref_is_read_first(string vouched)
    {
        await using Peers peers = await Peers.StartAsync();
        PeerChain peer = peers.Vouched(Guid.Parse(vouched));
        Fill(peer, 2);
        await peers.PushAsync(peers.Root, peer);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        Assert.Equal(new OriginImport(peer.Origin, 2, 2, null), Of(report, peer));
        Assert.Equal(new OriginImport(peers.Root.Origin, 2, 2, null), Of(report, peers.Root));
    }

    [Fact]
    public async Task A_vouch_this_nodes_export_has_written_and_not_pushed_admits_the_vouched_nodes_lines()
    {
        await using LedgerNode node = await LedgerNode.StartAsync();
        Guid id = Ids.New();
        await node.AddProjectAsync(Alpha, id, [node.RemotePath(Alpha)]);
        await node.TrustRootAsync(Alpha, node.Identity.Key.PublicKey);
        PeerChain peer = PeerChain.Of(node.Store, id);
        Fill(peer, 1);
        await peer.PushAsync(node.RemotePath(Alpha));
        await node.RecordRootAsync(Alpha);
        await node.RecordAsync(Alpha, new NodeVouched(peer.Origin, peer.Key.PublicKey, At));

        // Until this node's export writes its vouch on a line, the vouch counts for nothing.
        Assert.Equal(ImportFaultKind.UntrustedSigner, Of(await node.Import.ImportAsync(Alpha, None), peer).Fault?.Kind);

        var unpushed = new LedgerExport(node.Store, node.Home, node.Identity, new PushRefused(node.ExportGit), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<LedgerExport>.Instance);
        LedgerPublication written = await unpushed.PublishAsync(Alpha, None);
        Assert.False(written.Published);
        Assert.True(written.Written > 0);
        Assert.Null(await node.RefAsync(node.RemotePath(Alpha)));

        Assert.Equal(new OriginImport(peer.Origin, 1, 1, null), Of(await node.Import.ImportAsync(Alpha, None), peer));
    }

    [Theory]
    [InlineData("unknown-type")]
    [InlineData("upgraded")]
    [InlineData("registration-with-repos")]
    [InlineData("registration-without-repos")]
    [InlineData("earlier-fork-base")]
    public async Task A_line_in_an_earlier_shape_stops_its_origin(string shape)
    {
        await using Peers peers = await Peers.StartAsync();
        PeerChain peer = peers.Vouched();
        Fill(peer, 1);
        Guid definition = Ids.New();
        string drafted = peer.Stored(Drafted(definition, null)).Data;
        StoredEvent earlier = shape switch
        {
            "unknown-type" => new StoredEvent("project_required_checks_set", """{"checks":["build"]}"""),
            "upgraded" => new StoredEvent("definition_drafted", drafted.Replace("\"scope\":\"Project\"", "\"scope\":\"Shipped\"", StringComparison.Ordinal)),
            "registration-with-repos" => new StoredEvent("project_registered", $$"""{"projectId":"{{peers.ProjectId}}","name":"alpha","repos":[{"name":"repo","remote":"r"}],"registeredBy":"owner","registeredAt":"2026-11-05T12:00:00+00:00","primaryBranch":"main"}"""),
            "registration-without-repos" => new StoredEvent("project_registered", $$"""{"projectId":"{{peers.ProjectId}}","name":"alpha","registeredBy":"owner","registeredAt":"2026-11-05T12:00:00+00:00","remote":"r"}"""),
            "earlier-fork-base" => peer.Stored(Drafted(definition, ForkBase.Earlier("build", 1))),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        Assert.NotEqual(drafted, earlier.Data);
        Guid stream = shape.StartsWith("registration", StringComparison.Ordinal)
            ? peers.ProjectId
            : definition;
        peer.Add(earlier, stream);
        Fill(peer, 1);
        await peers.PushAsync(peers.Root, peer);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, peer, ImportFaultKind.EarlierShape, at: 2, stored: 1);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("4294967296")]
    public async Task A_line_from_any_key_whose_data_cannot_be_judged_stops_only_its_origin(string kind)
    {
        await using Peers peers = await Peers.StartAsync();
        // An origin no key vouches for, whose ref is read first, carrying a step kind no step kind reads as.
        PeerChain stranger = PeerChain.Of(peers.Node.Store, peers.ProjectId, Guid.Empty);
        Guid flowRun = Ids.New();
        string started = stranger.Stored(new StepStarted(flowRun, "gates", StepKind.SystemStep, "gates", At)).Data;
        string unjudged = Regex.Replace(started, "\"kind\":[^,]+", $"\"kind\":{kind}");
        Assert.NotEqual(started, unjudged);
        stranger.Add(new StoredEvent("step_started", unjudged), flowRun);
        PeerChain sound = peers.Vouched();
        Fill(sound, 1);
        await peers.PushAsync(peers.Root, stranger, sound);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        AssertStopped(report, stranger, ImportFaultKind.EarlierShape, at: 1, stored: 0);
        Assert.Contains("could not be judged", Of(report, stranger).Fault?.Reason, StringComparison.Ordinal);
        Assert.Equal(new OriginImport(sound.Origin, 1, 1, null), Of(report, sound));
    }

    [Fact]
    public async Task A_flow_naming_a_software_step_unprefixed_and_an_agent_step_spawned_with_null_members_are_current_and_stored()
    {
        await using Peers peers = await Peers.StartAsync();
        PeerChain peer = peers.Vouched();
        Guid definition = Ids.New();
        Guid flowRun = Ids.New();
        peer.Add(Drafted(definition, null, """{"name":"build","steps":[{"name":"worktree","systemStep":"worktree"}]}"""), definition);
        peer.Add(new FlowRunStarted(flowRun, Ids.New(), "build", 1, "sha256:flow", "peer", "implement", StepKind.AgentStep, "implementer", At), flowRun);
        peer.Add(new AgentStepSpawned(flowRun, "implement", "implementer", null, null, At, null), flowRun);
        await peers.PushAsync(peers.Root, peer);

        LedgerImportReport report = await peers.Node.Import.ImportAsync(Alpha, None);

        Assert.Equal(new OriginImport(peer.Origin, 3, 3, null), Of(report, peer));
        IReadOnlyList<IEvent> events = await peers.Node.EventsAsync(Alpha);
        Assert.Contains("\"systemStep\":\"worktree\"", Assert.IsType<DefinitionDrafted>(Assert.Single(events, e => e.StreamId == definition).Data).Content, StringComparison.Ordinal);
        AgentStepSpawned spawned = Assert.IsType<AgentStepSpawned>(Assert.Single(events, e => e.EventTypeName == "agent_step_spawned").Data);
        Assert.Equal((null, null, null), (spawned.AgentHash, spawned.Model, spawned.LaunchId));
    }

    [Fact]
    public async Task A_second_node_importing_the_first_nodes_lines_reads_its_parcel_and_flow_run_as_the_first_does()
    {
        await using LedgerNode first = await LedgerNode.StartAsync(Alpha);
        await first.RecordRootAsync(Alpha);
        var parcels = new ParcelService(first.Store, TimeProvider.System);
        var flowRuns = new FlowRunService(first.Store, TimeProvider.System);
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Alpha, "task", "Carried across", null, None);
        await parcels.SetFieldAsync(Principal.LocalOwner, Alpha, parcel.Id, TaskFields.Objective, TaskFields.ObjectiveValue, None);
        await parcels.SetFieldAsync(Principal.LocalOwner, Alpha, parcel.Id, TaskFields.Criteria, TaskFields.CriteriaValue, None);
        await parcels.MoveAsync(Principal.LocalOwner, Alpha, parcel.Id, "build", None);
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Alpha, parcel.Id, SoftwareFlows.Build.Name, None);
        await flowRuns.EndAsync(Principal.LocalOwner, Alpha, started.Id, SoftwareFlows.Build.Outcome, None);
        LedgerPublication published = await first.Export.PublishAsync(Alpha, None);
        Assert.True(published.Published, string.Join("; ", published.Failures));

        // The second home holds the first's project id and name, a clone of the same stand-in remote, and the
        // first node's key as the root it trusts.
        await using LedgerNode second = await LedgerNode.StartAsync();
        Project project = await first.ProjectAsync(Alpha);
        await second.AddProjectAsync(Alpha, project.Id, [first.RemotePath(Alpha)]);
        await second.TrustRootAsync(Alpha, first.Identity.Key.PublicKey);

        LedgerImportReport report = await second.Import.ImportAsync(Alpha, None);

        Assert.Equal([new OriginImport(first.Identity.NodeId, published.Written, published.Written, null)], report.Origins);
        Parcel? imported = await new ParcelService(second.Store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Alpha, parcel.Id, None);
        Parcel? original = await parcels.ShowAsync(Principal.LocalOwner, Alpha, parcel.Id, None);
        Assert.Equal(Read(first, original), Read(second, imported));
        FlowRun? importedRun = await new FlowRunService(second.Store, TimeProvider.System).ShowAsync(Principal.LocalOwner, Alpha, started.Id, None);
        FlowRun? originalRun = await flowRuns.ShowAsync(Principal.LocalOwner, Alpha, started.Id, None);
        Assert.Equal(Read(first, originalRun), Read(second, importedRun));
        Assert.Equal(FlowRunStatus.Ended, importedRun?.Status);
    }

    [Fact]
    public async Task While_the_daemon_runs_a_line_pushed_to_a_peers_ref_is_stored_within_its_import_interval()
    {
        using var temp = new TemporaryHome();
        string remote = Path.Combine(temp.Home.Path, "remotes", Alpha, $"{RegisteredProjects.Repo}.git");
        Directory.CreateDirectory(remote);
        await LedgerNode.GitAsync(remote, "init", "--bare", "--quiet");
        TimeSpan interval = TimeSpan.FromMilliseconds(250);
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { CommandRunner = new ProcessCommandRunner(), ImportInterval = interval }, None);
        Project project = await daemon.Services.GetRequiredService<ProjectService>().AddAsync(Principal.LocalOwner, Alpha, remote, RegisteredProjects.Settings, None);
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        PeerChain root = PeerChain.Of(store, project.Id);
        root.Add(new OwnerRootRecorded(root.Origin, root.Key.PublicKey, At));
        await using (IDocumentSession session = store.LightweightSession(Alpha))
        {
            session.Store(new LedgerRoot { Key = root.Key.PublicKey });
            await session.SaveChangesAsync(None);
        }

        root.Add(Created(out Guid parcel), parcel);
        await root.PushAsync(remote);

        // Generous against a loaded machine: the loop itself comes round every quarter of a second.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        IReadOnlyList<LedgerLine> records = [];
        while (DateTimeOffset.UtcNow < deadline && records.Count < root.Lines.Count)
        {
            await Task.Delay(interval, None);
            await using IQuerySession session = store.QuerySession(Alpha);
            records = await session.Query<LedgerLine>().Where(l => l.Origin == root.Origin).ToListAsync(None);
        }

        Assert.Equal([1L, 2L], records.Select(r => r.Sequence).Order());
        await using (IQuerySession session = store.QuerySession(Alpha))
        {
            Assert.NotNull(await session.LoadAsync<Parcel>(parcel, None));
        }

        await daemon.StopAsync(None);
        foreach (FileInfo file in new DirectoryInfo(temp.Home.Path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    /// <summary>A document as the store writes it, which is how a service reads it.</summary>
    private static string Read(LedgerNode node, object? document) =>
        document is null
            ? "(none)"
            : node.Store.Options.Serializer.ToJson(document);

    private static ParcelCreated Created(out Guid parcel, string title = "Parcel")
    {
        parcel = Ids.New();
        return new ParcelCreated(parcel, Alpha, "task", "sha256:type", "inbox", title, "peer", At);
    }

    private static DefinitionDrafted Drafted(Guid definition, ForkBase? forkBase, string content = """{"name":"build"}""") =>
        new(definition, DefinitionKind.Flow, "build", DefinitionScope.Project, Alpha, 1, content, "sha256:content", forkBase, "peer", At);

    /// <summary>Appends <paramref name="count"/> parcel creations to <paramref name="chain"/>, titled by their sequence.</summary>
    private static void Fill(PeerChain chain, int count)
    {
        for (int i = 0; i < count; i++)
        {
            chain.Add(Created(out Guid parcel, $"Parcel {chain.Lines.Count + 1}"), parcel);
        }
    }

    private static OriginImport Of(LedgerImportReport report, PeerChain chain) => Assert.Single(report.Origins, o => o.Origin == chain.Origin);

    private static void AssertStopped(LedgerImportReport report, PeerChain chain, ImportFaultKind kind, long at, int stored, long? held = null)
    {
        OriginImport origin = Of(report, chain);
        Assert.Equal((kind, (long?)at), (origin.Fault?.Kind, origin.Fault?.Sequence));
        long? before = at > 1
            ? at - 1
            : null;
        Assert.Equal((stored, held ?? before), (origin.Stored, origin.Held));
    }

    /// <summary>
    /// A node importing project <see cref="Alpha"/> under <paramref name="projectId"/> from the stand-in remote
    /// <paramref name="remote"/>, and a root origin whose key the node trusts once <see cref="StartAsync"/>
    /// seeds it, whose first line records it as the owner root and whose later lines vouch for other origins.
    /// </summary>
    private sealed class Peers(LedgerNode node, Guid projectId, string remote) : IAsyncDisposable
    {
        public LedgerNode Node => node;

        public Guid ProjectId => projectId;

        public string Remote => remote;

        public PeerChain Root { get; } = Rooted(node, projectId);

        public static async Task<Peers> StartAsync()
        {
            LedgerNode node = await LedgerNode.StartAsync();
            Guid id = Ids.New();
            await node.AddProjectAsync(Alpha, id, [node.RemotePath(Alpha)]);
            var peers = new Peers(node, id, node.RemotePath(Alpha));
            await node.TrustRootAsync(Alpha, peers.Root.Key.PublicKey);
            return peers;
        }

        /// <summary>A chain of a new origin, which the root vouches for in its next line.</summary>
        public PeerChain Vouched(Guid? origin = null)
        {
            PeerChain chain = PeerChain.Of(node.Store, projectId, origin);
            Root.Add(new NodeVouched(chain.Origin, chain.Key.PublicKey, At));
            return chain;
        }

        public async Task PushAsync(params PeerChain[] chains)
        {
            foreach (PeerChain chain in chains)
            {
                await chain.PushAsync(remote);
            }
        }

        public ValueTask DisposeAsync() => node.DisposeAsync();

        private static PeerChain Rooted(LedgerNode node, Guid projectId)
        {
            PeerChain root = PeerChain.Of(node.Store, projectId);
            root.Add(new OwnerRootRecorded(root.Origin, root.Key.PublicKey, At));
            return root;
        }
    }

    /// <summary>A command runner that refuses every push and runs every other command through another.</summary>
    private sealed class PushRefused(ICommandRunner inner) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken) =>
            command.Arguments.Contains("push")
                ? Task.FromResult(new CommandResult(1, string.Empty, "push refused for the test", "push refused for the test"))
                : inner.RunAsync(command, cancellationToken);
    }
}
