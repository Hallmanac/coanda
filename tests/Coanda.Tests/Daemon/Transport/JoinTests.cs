using System.Text;
using Coanda.Core;
using Coanda.Core.Identity.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Identity;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// A second node joins the owner's project from the project's remote, a local bare repository standing in for
/// it, and the fingerprint of the owner's root key, which the owner's first vouch records as the project's root.
/// </summary>
public sealed class JoinTests
{
    private const string Alpha = "alpha";
    private static readonly CancellationToken None = LedgerNode.None;
    private static readonly Principal Owner = Principal.LocalOwner;

    [Fact]
    public async Task A_joined_node_holds_the_owners_registration_and_parcels_and_the_owner_imports_what_it_adds()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();

        // The owner registers the project and adds parcels before its first vouch.
        Guid[] before = [await AddAsync(owner, "First"), await AddAsync(owner, "Second")];
        NodeVouchResult vouched = await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);
        Assert.Equal((true, true, true, (string?)null), (vouched.Recorded, vouched.RootRecorded, vouched.Pushed, vouched.Reason));

        // The owner still records in the project after its vouch.
        Guid after = await AddAsync(owner, "After the vouch");
        await PublishAsync(owner);

        JoinResult joined = await joining.Joiner.JoinAsync(Owner, owner.RemotePath(Alpha), owner.Fingerprint, None);

        Assert.Equal((Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, false, true), (joined.Project, joined.NodeId, joined.PublicKey, joined.AlreadyHeld, joined.Trusted));
        Assert.Empty(joined.Failures);
        OriginImport fromOwner = Assert.Single(joined.Origins);
        Assert.Equal(owner.Identity.NodeId, fromOwner.Origin);
        Assert.Null(fromOwner.Fault);
        Assert.Equal((await owner.RecordsAsync(Alpha)).Count, fromOwner.Stored);
        Assert.Equal(await TitlesAsync(owner), await TitlesAsync(joining));
        Assert.Superset(new HashSet<Guid>([.. before, after]), (await ParcelsAsync(joining)).Select(p => p.Id).ToHashSet());

        // The project's one registration is the owner's, stored from the owner's line under the owner's id and name.
        Project ownerProject = await owner.ProjectAsync(Alpha);
        Project project = await joining.ProjectAsync(Alpha);
        Assert.Equal((ownerProject.Id, ownerProject.Name), (project.Id, project.Name));
        IEvent registration = Assert.Single(await joining.EventsAsync(Alpha), e => e.Data is ProjectRegistered);
        Assert.Equal(owner.Identity.NodeId, Assert.Single(await joining.RecordsAsync(Alpha), r => r.Id == registration.Id).Origin);

        // The clone is at the layout path with the remote as its origin, and the root key record names the owner's key.
        Assert.Equal(owner.RemotePath(Alpha), (await LedgerNode.GitAsync(joining.ClonePath(Alpha, RegisteredProjects.Repo), "remote", "get-url", "origin")).Trim());
        Assert.Equal(owner.Identity.Key.PublicKey, (await RootOfAsync(joining))?.Key);
        Assert.Equal(Owner.Id, NodeOwnerRecord.Read(joining.Home));
        Assert.Empty(Directory.EnumerateDirectories(joining.Home.Path, ".join-*"));

        // What the joined node adds reaches the owner, and its own ref holds no registration of its own.
        Guid added = await AddAsync(joining, "From the joined node");
        await PublishAsync(joining);
        IReadOnlyList<SignedLine> joinedLines = await joining.ParsedAsync(owner.RemotePath(Alpha));
        Assert.NotEmpty(joinedLines);
        Assert.DoesNotContain(joinedLines, l => l.Content.Event.Type == "project_registered");
        Assert.All(joinedLines, l => Assert.Equal(ownerProject.Id, l.Content.ProjectId));

        await ImportAsync(owner);
        Assert.Contains(added, (await ParcelsAsync(owner)).Select(p => p.Id));
    }

    [Fact]
    public async Task A_join_with_a_fingerprint_no_root_record_has_is_refused_and_leaves_nothing_behind()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();
        await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => joining.Joiner.JoinAsync(Owner, owner.RemotePath(Alpha), joining.Fingerprint, None));

        Assert.Contains($"no owner root record on {owner.RemotePath(Alpha)} names a key with the fingerprint {joining.Fingerprint}", refused.Message, StringComparison.Ordinal);
        await NothingLeftAsync(joining);
    }

    [Fact]
    public async Task A_join_whose_fetch_fails_is_refused_and_leaves_nothing_behind()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();
        await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);

        string missing = Path.Combine(owner.Home.Path, "no-such-remote.git");
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => joining.Joiner.JoinAsync(Owner, missing, owner.Fingerprint, None));

        Assert.Contains($"the ledger refs of {missing} could not be fetched", refused.Message, StringComparison.Ordinal);
        await NothingLeftAsync(joining);
    }

    [Fact]
    public async Task A_join_with_a_remote_other_than_one_the_registration_records_is_refused_and_leaves_nothing_behind()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();
        await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);

        // The same repository, by a form of its remote the registration does not record.
        string other = owner.RemotePath(Alpha) + Path.DirectorySeparatorChar;
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => joining.Joiner.JoinAsync(Owner, other, owner.Fingerprint, None));

        Assert.Contains($"{other} is not exactly the remote of any repo of project '{Alpha}'", refused.Message, StringComparison.Ordinal);
        await NothingLeftAsync(joining);
    }

    [Fact]
    public async Task A_join_of_a_project_registered_here_under_another_id_is_refused()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();
        await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);
        Project own = await joining.AddProjectAsync(Alpha, Ids.New(), [Path.Combine(joining.Home.Path, "remotes", "own.git")]);
        int events = (await joining.EventsAsync(Alpha)).Count;

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => joining.Joiner.JoinAsync(Owner, owner.RemotePath(Alpha), owner.Fingerprint, None));

        Assert.Contains($"project '{Alpha}' is already registered on this node under the id {own.Id}", refused.Message, StringComparison.Ordinal);
        Assert.Equal(events, (await joining.EventsAsync(Alpha)).Count);
        Assert.Null(await RootOfAsync(joining));
        Assert.False(Directory.Exists(joining.ClonePath(Alpha, RegisteredProjects.Repo)));
        Assert.Null(NodeOwnerRecord.Read(joining.Home));
        Assert.Empty(Directory.EnumerateDirectories(joining.Home.Path, ".join-*"));
    }

    [Fact]
    public async Task A_join_on_a_node_that_records_another_owner_id_is_refused_and_leaves_nothing_behind()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();
        await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);
        NodeOwnerRecord.Write(joining.Home, "someone-else");

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => joining.JoinerOf(NodeOwner.Of(joining.Home)).JoinAsync(Owner, owner.RemotePath(Alpha), owner.Fingerprint, None));

        Assert.Contains($"This node already records the owner id 'someone-else', not '{Owner.Id}'", refused.Message, StringComparison.Ordinal);
        Assert.Equal("someone-else", NodeOwnerRecord.Read(joining.Home));
        File.Delete(joining.Home.NodeOwnerPath);
        await NothingLeftAsync(joining);
    }

    [Fact]
    public async Task A_join_whose_root_chain_does_not_verify_through_the_root_record_and_the_registration_is_refused()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();
        await AddAsync(owner, "Before the root");
        await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);

        // A line before the root record is changed on the remote, so its signature no longer verifies.
        IReadOnlyDictionary<string, string> files = await owner.FilesAsync(owner.RemotePath(Alpha));
        (string segment, string content) = files.Single();
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int parcelLine = Array.FindIndex(lines, l => l.Contains("Before the root", StringComparison.Ordinal));
        lines[parcelLine] = lines[parcelLine].Replace("Before the root", "Before the ruut", StringComparison.Ordinal);
        await WriteRefAsync(owner.RemotePath(Alpha), owner.RefName, new Dictionary<string, string> { [segment] = string.Join('\n', lines) + "\n" });

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => joining.Joiner.JoinAsync(Owner, owner.RemotePath(Alpha), owner.Fingerprint, None));

        Assert.Contains($"the chain of node {owner.Identity.NodeId} does not verify, signed by the root key, through its root record", refused.Message, StringComparison.Ordinal);
        await NothingLeftAsync(joining);
    }

    [Fact]
    public async Task A_join_of_a_remote_whose_lines_root_two_projects_at_the_key_is_refused()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha, "beta");
        await using LedgerNode joining = await LedgerNode.StartAsync();
        await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);
        await owner.Voucher.VouchAsync(Owner, "beta", joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);

        // The owner's ref on alpha's remote carries beta's chain too, root record and all.
        (string segment, string alpha) = (await owner.FilesAsync(owner.RemotePath(Alpha))).Single();
        string beta = (await owner.FilesAsync(owner.RemotePath("beta"))).Single().Value;
        await WriteRefAsync(owner.RemotePath(Alpha), owner.RefName, new Dictionary<string, string> { [segment] = alpha + beta });

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => joining.Joiner.JoinAsync(Owner, owner.RemotePath(Alpha), owner.Fingerprint, None));

        Assert.Contains("root more than one project at the key", refused.Message, StringComparison.Ordinal);
        await NothingLeftAsync(joining);
    }

    [Fact]
    public async Task A_join_of_a_project_this_node_already_holds_changes_nothing_and_says_so()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();
        await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);
        Assert.False((await joining.Joiner.JoinAsync(Owner, owner.RemotePath(Alpha), owner.Fingerprint, None)).AlreadyHeld);
        int events = (await joining.EventsAsync(Alpha)).Count;

        JoinResult again = await joining.Joiner.JoinAsync(Owner, owner.RemotePath(Alpha), owner.Fingerprint, None);

        Assert.True(again.AlreadyHeld);
        Assert.True(again.Trusted);
        Assert.Empty(again.Origins);
        Assert.Equal(events, (await joining.EventsAsync(Alpha)).Count);
        Assert.Empty(Directory.EnumerateDirectories(joining.Home.Path, ".join-*"));
    }

    [Fact]
    public async Task A_node_joined_before_its_vouch_is_stored_by_the_owner_only_once_the_owner_vouches_for_it()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joining = await LedgerNode.StartAsync();

        // The root is recorded by a vouch for another node, so the joining node's own vouch does not exist yet.
        NodeIdentity other = await LedgerNode.OtherIdentityAsync();
        await owner.Voucher.VouchAsync(Owner, Alpha, other.NodeId, other.Key.PublicKey, None);

        JoinResult joined = await joining.Joiner.JoinAsync(Owner, owner.RemotePath(Alpha), owner.Fingerprint, None);
        Assert.False(joined.Trusted);
        Assert.Null(Assert.Single(joined.Origins).Fault);

        Guid added = await AddAsync(joining, "Before the vouch");
        await PublishAsync(joining);

        LedgerImportReport refused = await owner.Import.ImportAsync(Alpha, None);
        OriginImport fromJoined = Assert.Single(refused.Origins, o => o.Origin == joining.Identity.NodeId);
        Assert.Equal((0, ImportFaultKind.UntrustedSigner), (fromJoined.Stored, fromJoined.Fault?.Kind));
        Assert.DoesNotContain(added, (await ParcelsAsync(owner)).Select(p => p.Id));

        NodeVouchResult vouched = await owner.Voucher.VouchAsync(Owner, Alpha, joining.Identity.NodeId, joining.Identity.Key.PublicKey, None);
        Assert.Equal((true, false, true), (vouched.Recorded, vouched.RootRecorded, vouched.Pushed));
        await ImportAsync(owner);
        Assert.Contains(added, (await ParcelsAsync(owner)).Select(p => p.Id));
    }

    [Fact]
    public async Task A_run_carrying_no_root_record_that_names_the_trusted_key_stores_nothing_from_its_origin()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode peer = await LedgerNode.StartAsync();

        // The owner's chain vouches for the peer without ever recording a root.
        await AddAsync(owner, "Rootless");
        await owner.RecordAsync(Alpha, new NodeVouched(peer.Identity.NodeId, peer.Identity.Key.PublicKey, DateTimeOffset.UtcNow));
        await PublishAsync(owner);

        Project project = await owner.ProjectAsync(Alpha);
        await peer.AddProjectAsync(Alpha, project.Id, [owner.RemotePath(Alpha)]);
        await peer.TrustRootAsync(Alpha, owner.Identity.Key.PublicKey);
        int events = (await peer.EventsAsync(Alpha)).Count;

        OriginImport imported = Assert.Single((await peer.Import.ImportAsync(Alpha, None)).Origins);

        Assert.Equal((0, ImportFaultKind.UntrustedSigner), (imported.Stored, imported.Fault?.Kind));
        Assert.Equal(events, (await peer.EventsAsync(Alpha)).Count);
    }

    private static async Task NothingLeftAsync(LedgerNode node)
    {
        Assert.Empty(await node.EventsAsync(Alpha));
        Assert.Null(await RootOfAsync(node));
        await using (IQuerySession session = node.Store.QuerySession(Tenants.Node))
        {
            Assert.Empty(await session.Query<Project>().AnyTenant().ToListAsync(None));
        }

        Assert.False(Directory.Exists(Path.Combine(node.Home.Path, "projects")) && Directory.EnumerateFileSystemEntries(Path.Combine(node.Home.Path, "projects")).Any());
        Assert.Empty(Directory.EnumerateDirectories(node.Home.Path, ".join-*"));
        Assert.Null(NodeOwnerRecord.Read(node.Home));
    }

    private static async Task<LedgerRoot?> RootOfAsync(LedgerNode node)
    {
        await using IQuerySession session = node.Store.QuerySession(Alpha);
        return await session.LoadAsync<LedgerRoot>(LedgerRoot.DocumentId, None);
    }

    private static async Task<Guid> AddAsync(LedgerNode node, string title) =>
        (await new ParcelService(node.Store, TimeProvider.System).AddAsync(Owner, Alpha, "task", title, null, None)).Id;

    private static async Task<IReadOnlyList<Parcel>> ParcelsAsync(LedgerNode node) =>
        await new ParcelService(node.Store, TimeProvider.System).ListAsync(Owner, Alpha, None);

    private static async Task<IReadOnlyList<string>> TitlesAsync(LedgerNode node) =>
        [.. (await ParcelsAsync(node)).Select(p => p.Title).Order(StringComparer.Ordinal)];

    private static async Task PublishAsync(LedgerNode node)
    {
        LedgerPublication published = await node.Export.PublishAsync(Alpha, None);
        Assert.True(published.Published, string.Join("; ", published.Failures));
    }

    private static async Task ImportAsync(LedgerNode node)
    {
        LedgerImportReport report = await node.Import.ImportAsync(Alpha, None);
        Assert.Empty(report.Failures);
        Assert.All(report.Origins, o => Assert.Null(o.Fault));
    }

    /// <summary>Points <paramref name="refName"/> in the repository at a commit of its own holding <paramref name="files"/>, as a remote rewritten by hand would.</summary>
    internal static async Task WriteRefAsync(string repository, string refName, IReadOnlyDictionary<string, string> files)
    {
        string scratch = Directory.CreateTempSubdirectory("coanda-join-test-").FullName;
        try
        {
            var variables = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = Path.Combine(scratch, "index"),
                ["GIT_AUTHOR_NAME"] = "test",
                ["GIT_AUTHOR_EMAIL"] = "test@example.com",
                ["GIT_COMMITTER_NAME"] = "test",
                ["GIT_COMMITTER_EMAIL"] = "test@example.com",
            };
            foreach ((string name, string content) in files)
            {
                string path = Path.Combine(scratch, name);
                await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(content));
                string blob = (await LedgerNode.GitAsync(repository, "hash-object", "-w", path)).Trim();
                await LedgerNode.GitAsync(repository, variables, "update-index", "--add", "--cacheinfo", $"100644,{blob},{name}");
            }

            string tree = (await LedgerNode.GitAsync(repository, variables, "write-tree")).Trim();
            string commit = (await LedgerNode.GitAsync(repository, variables, "commit-tree", tree, "-m", "rewritten")).Trim();
            await LedgerNode.GitAsync(repository, "update-ref", refName, commit);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }
}
