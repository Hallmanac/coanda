using System.Text;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Coanda.Tests.Core;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// The node's export: each project's events become one signed chain on <c>refs/coanda/nodes/&lt;node-id&gt;</c>
/// in every clone, recorded line by line in the store, tagged from the parcel's tag field, and pushed to a
/// local bare repository standing in for the remote.
/// </summary>
public sealed class LedgerExportTests
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private static readonly CancellationToken None = LedgerNode.None;

    private static ParcelService Parcels(LedgerNode node) => new(node.Store, TimeProvider.System);

    private static Task<Parcel> SetReposAsync(LedgerNode node, Parcel parcel, params string[] repos) =>
        Parcels(node).SetFieldAsync(Principal.LocalOwner, Alpha, parcel.Id, "repos", FieldValue.OfItems(repos), None);

    private static ChainVerification Verify(LedgerNode node, Project project, IReadOnlyList<byte[]> lines, HeldPosition? held = null) =>
        ChainVerifier.Verify(lines, project.Id, node.Identity.NodeId, [node.Identity.Key.PublicKey], held);

    [Fact]
    public async Task An_export_writes_each_project_event_once_as_a_chain_that_verifies_and_pushes_the_nodes_ref_alone()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        Project alpha = await node.ProjectAsync(Alpha);
        await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "First", null, None);

        LedgerPublication first = await node.Export.PublishAsync(Alpha, None);

        IReadOnlyList<IEvent> events = await node.EventsAsync(Alpha);
        Assert.True(first.Published, string.Join("; ", first.Failures));
        Assert.Equal(events.Count, first.Written);
        string remote = node.RemotePath(Alpha);
        Assert.Equal(await node.FilesAsync(node.ClonePath(Alpha)), await node.FilesAsync(remote));
        Assert.Equal([node.RefName], (await LedgerNode.GitAsync(remote, "for-each-ref", "--format=%(refname)")).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        IReadOnlyList<byte[]> lines = await node.LinesAsync(remote);
        ChainVerification verified = Verify(node, alpha, lines);
        Assert.Equal((ChainOutcome.Verified, events.Count), (verified.Outcome, verified.VerifiedLines));
        IReadOnlyList<SignedLine> parsed = await node.ParsedAsync(remote);
        Assert.Equal(
            events.Select((e, i) => (i + 1L, e.StreamId, e.Id, e.Timestamp.ToUniversalTime(), e.EventTypeName)),
            parsed.Select(l => (l.Content.Sequence, l.Content.StreamId, l.Content.EventId, l.Content.RecordedAt, l.Content.Event.Type)));
        Assert.Equal(
            parsed.Select(l => (node.Identity.NodeId, l.Content.Sequence, l.Content.PreviousHash, l.Content.Tag.RepoNames, l.Signature, l.Hash)),
            (await node.RecordsAsync(Alpha)).Select(r => (r.Origin, r.Sequence, r.PreviousHash, r.Tag, r.Signature, r.Hash)));
        Assert.Equal(DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl", Assert.Single(await node.FilesAsync(remote)).Key);

        // Nothing new leaves the ref as it was, here and on the remote.
        string? written = await node.RefAsync(node.ClonePath(Alpha));
        LedgerPublication again = await node.Export.PublishAsync(Alpha, None);
        Assert.Equal((true, 0), (again.Published, again.Written));
        Assert.Equal(written, await node.RefAsync(node.ClonePath(Alpha)));
        Assert.Equal(written, await node.RefAsync(remote));

        // New events are appended after the lines already written, and only they are.
        await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "Second", null, None);
        LedgerPublication third = await node.Export.PublishAsync(Alpha, None);
        IReadOnlyList<IEvent> later = [.. (await node.EventsAsync(Alpha)).Skip(events.Count)];
        Assert.Equal((true, later.Count), (third.Published, third.Written));
        IReadOnlyList<byte[]> extended = await node.LinesAsync(remote);
        Assert.Equal(lines.Select(Encoding.UTF8.GetString), extended.Take(lines.Count).Select(Encoding.UTF8.GetString));
        Assert.Equal(later.Select(e => (Guid?)e.Id), extended.Skip(lines.Count).Select(l => SignedLine.Parse(l, out _)?.Content.EventId));
        Assert.Equal(ChainOutcome.Verified, Verify(node, alpha, extended).Outcome);
        Assert.Equal(ChainOutcome.Verified, Verify(node, alpha, [.. extended.Skip(lines.Count)], new HeldPosition(lines.Count, SignedLine.HashOf(lines[^1]))).Outcome);
    }

    [Fact]
    public async Task A_parcels_lines_carry_the_repos_its_tag_field_holds_at_export_and_a_set_of_it_carries_the_old_and_new_ones_too()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        await PreviousFlows.PublishAsync(node.Store, Alpha, PreviousFlows.Build);
        Parcel tagged = await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "Tagged", null, None);
        await SetReposAsync(node, tagged, "repo");
        Parcel untagged = await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "Untagged", null, None);
        Parcel idea = await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "idea", "An idea", null, None);
        await node.Export.PublishAsync(Alpha, None);

        // Read at export: the creation was recorded before the field was set, and carries what it holds now.
        IReadOnlyList<SignedLine> first = await node.ParsedAsync(node.RemotePath(Alpha));
        Assert.All(first.Where(l => l.Content.StreamId == tagged.Id), l => Assert.Equal(["repo"], l.Content.Tag.RepoNames));
        Assert.All(first.Where(l => l.Content.StreamId != tagged.Id), l => Assert.True(l.Content.Tag.IsProjectWide, $"{l.Content.Event.Type} of stream {l.Content.StreamId}"));
        Assert.Contains(first, l => l.Content.StreamId == untagged.Id);
        Assert.Contains(first, l => l.Content.StreamId == idea.Id);

        // A retag is a set of the tag field, carrying the repos the field held before and holds now; the
        // flow run's events, here seeded as a build from before the carve-out recorded them, carry the parcel's.
        await SetReposAsync(node, tagged, "web");
        await Parcels(node).SetFieldAsync(Principal.LocalOwner, Alpha, tagged.Id, "objective", FieldValue.OfText("Ship it"), None);
        (Guid flowRunId, _) = await EarlierFlowRuns.DispatchedAsync(node.Store, Alpha, tagged.Id, None);
        await node.Export.PublishAsync(Alpha, None);

        IReadOnlyList<SignedLine> all = await node.ParsedAsync(node.RemotePath(Alpha));
        Assert.Equal(first.Select(l => Encoding.UTF8.GetString(l.Bytes)), all.Take(first.Count).Select(l => Encoding.UTF8.GetString(l.Bytes)));
        List<SignedLine> added = [.. all.Skip(first.Count)];
        Assert.Equal(
            [
                ("parcel_field_set", tagged.Id, "repo,web"),
                ("parcel_field_set", tagged.Id, "web"),
                ("parcel_flow_run_attached", tagged.Id, "web"),
                ("flow_run_started", flowRunId, "web"),
                ("system_step_dispatched", flowRunId, "web"),
            ],
            added.Select(l => (l.Content.Event.Type, l.Content.StreamId, string.Join(',', l.Content.Tag.RepoNames ?? []))));
        Assert.All(
            added.Where(l => l.Content.StreamId == flowRunId),
            l => Assert.Equal("software/worktree", EarlierFlowRuns.SystemStepOf(new StoredRow(0, "", 0, l.Content.Event.Type, l.Content.Event.Data, Alpha, 0))));
    }

    [Fact]
    public async Task A_set_of_the_tag_field_carries_the_repos_it_wrote_when_a_later_set_moved_the_parcel_on_before_the_export()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        Parcel parcel = await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "Moved twice", null, None);
        await SetReposAsync(node, parcel, "a");
        await SetReposAsync(node, parcel, "b");
        await SetReposAsync(node, parcel, "c");
        await node.Export.PublishAsync(Alpha, None);

        IReadOnlyList<SignedLine> lines = await node.ParsedAsync(node.RemotePath(Alpha));
        Assert.Equal(
            ["a,c", "a,b,c", "b,c"],
            lines.Where(l => l.Content.StreamId == parcel.Id && l.Content.Event.Type == "parcel_field_set").Select(l => string.Join(',', l.Content.Tag.RepoNames ?? [])));
    }

    [Fact]
    public async Task An_event_the_store_records_on_another_origins_line_is_never_written_as_this_nodes()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        Parcel imported = await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "From another node", null, None);
        IEvent created = (await node.EventsAsync(Alpha)).Single(e => e.StreamId == imported.Id);
        Guid other = (await LedgerNode.OtherIdentityAsync()).NodeId;
        await using (IDocumentSession session = node.Store.LightweightSession(Alpha))
        {
            session.Store(new LedgerLine { Id = created.Id, Origin = other, Sequence = 1, Signature = "seeded", Hash = "seeded", Segment = "2026-11", Line = "{}", StoreSequence = created.Sequence });
            await session.SaveChangesAsync(None);
        }

        LedgerPublication published = await node.Export.PublishAsync(Alpha, None);

        IReadOnlyList<SignedLine> lines = await node.ParsedAsync(node.RemotePath(Alpha));
        Assert.True(published.Published, string.Join("; ", published.Failures));
        Assert.Equal((await node.EventsAsync(Alpha)).Count - 1, lines.Count);
        Assert.DoesNotContain(lines, l => l.Content.EventId == created.Id);
        Assert.Equal(ChainOutcome.Verified, Verify(node, await node.ProjectAsync(Alpha), [.. lines.Select(l => l.Bytes)]).Outcome);
        Assert.Equal(other, (await node.RecordsAsync(Alpha)).Single(r => r.Id == created.Id).Origin);
    }

    [Fact]
    public async Task A_ref_deleted_or_rewound_in_the_clone_is_written_again_byte_for_byte_and_pushes_over_the_remote()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        string clone = node.ClonePath(Alpha);
        string remote = node.RemotePath(Alpha);
        Parcel parcel = await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "Rebuilt", null, None);
        await SetReposAsync(node, parcel, "repo");
        await node.Export.PublishAsync(Alpha, None);
        string firstCommit = await node.RefAsync(clone) ?? throw new InvalidOperationException("No ref was written.");

        // The tag field changes after its lines were first written; those lines keep the tag they were signed with.
        await SetReposAsync(node, parcel, "web");
        await node.Export.PublishAsync(Alpha, None);
        IReadOnlyDictionary<string, string> files = await node.FilesAsync(clone);

        await LedgerNode.GitAsync(clone, "update-ref", "-d", node.RefName);
        LedgerPublication deleted = await node.Export.PublishAsync(Alpha, None);
        Assert.True(deleted.Published, string.Join("; ", deleted.Failures));
        Assert.Equal(files, await node.FilesAsync(clone));
        Assert.Equal(files, await node.FilesAsync(remote));

        await LedgerNode.GitAsync(clone, "update-ref", node.RefName, firstCommit);
        LedgerPublication rewound = await node.Export.PublishAsync(Alpha, None);
        Assert.True(rewound.Published, string.Join("; ", rewound.Failures));
        Assert.Equal(files, await node.FilesAsync(clone));
        Assert.Equal(files, await node.FilesAsync(remote));

        // Rebuilt with no history the remote's shares, and holding lines the remote does not yet, it still pushes.
        await LedgerNode.GitAsync(clone, "update-ref", "-d", node.RefName);
        await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "After the rebuild", null, None);
        LedgerPublication extended = await node.Export.PublishAsync(Alpha, None);
        Assert.True(extended.Published, string.Join("; ", extended.Failures));
        IReadOnlyDictionary<string, string> after = await node.FilesAsync(remote);
        Assert.Equal(await node.FilesAsync(clone), after);
        Assert.All(files, file => Assert.StartsWith(file.Value, after[file.Key], StringComparison.Ordinal));
        Assert.True(after.Values.Sum(v => v.Length) > files.Values.Sum(v => v.Length));
    }

    [Fact]
    public async Task A_round_with_nothing_new_asks_git_only_for_each_clones_ref()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        await node.Export.PublishAsync(Alpha, None);
        node.ExportGit.Clear();

        LedgerPublication idle = await node.Export.PublishAsync(Alpha, None);

        Assert.Equal((true, 0), (idle.Published, idle.Written));
        Assert.Equal([$"rev-parse --verify --quiet {node.RefName}"], node.ExportGit.Calls);
    }

    [Fact]
    public async Task A_grown_chain_writes_again_only_the_files_of_its_last_segment_on_and_the_ref_holds_what_a_whole_write_gives()
    {
        await using LedgerNode node = await LedgerNode.StartAsync();
        await node.AddProjectAsync(Alpha, withRemote: false);
        string clone = node.ClonePath(Alpha);
        await node.Export.PublishAsync(Alpha, None);

        // The chain's first lines are recorded in an earlier month, so the ref holds two files.
        IReadOnlyList<LedgerLine> records = await node.RecordsAsync(Alpha);
        Assert.True(records.Count > 1);
        await using (IDocumentSession session = node.Store.LightweightSession(Alpha))
        {
            foreach (LedgerLine record in records.Take(records.Count - 1))
            {
                record.Segment = "2000-01";
                session.Store(record);
            }

            await session.SaveChangesAsync(None);
        }

        await LedgerNode.GitAsync(clone, "update-ref", "-d", node.RefName);
        await node.MakeRemoteAsync(Alpha);
        LedgerPublication whole = await node.Export.PublishAsync(Alpha, None);
        Assert.True(whole.Published, string.Join("; ", whole.Failures));
        string current = Assert.Single(records.Skip(records.Count - 1)).Segment + LedgerSegments.Extension;
        Assert.Equal(["2000-01" + LedgerSegments.Extension, current], (await node.FilesAsync(clone)).Keys);

        await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "Later", null, None);
        node.ExportGit.Clear();
        LedgerPublication grown = await node.Export.PublishAsync(Alpha, None);

        Assert.True(grown.Published, string.Join("; ", grown.Failures));
        string hashed = Assert.Single(node.ExportGit.Calls, c => c.StartsWith("hash-object -w ", StringComparison.Ordinal));
        Assert.EndsWith(Path.DirectorySeparatorChar + current, hashed, StringComparison.Ordinal);
        Assert.Single(hashed.Split(' '), part => part.EndsWith(LedgerSegments.Extension, StringComparison.Ordinal));
        Assert.Contains(node.ExportGit.Calls, c => c.StartsWith("read-tree ", StringComparison.Ordinal));

        IReadOnlyList<LedgerLine> all = await node.RecordsAsync(Alpha);
        IReadOnlyDictionary<string, string> expected = all
            .GroupBy(r => r.Segment)
            .ToDictionary(g => g.Key + LedgerSegments.Extension, g => string.Concat(g.Select(r => r.Line + "\n")));
        IReadOnlyDictionary<string, string> files = await node.FilesAsync(clone);
        Assert.Equal(expected.OrderBy(f => f.Key, StringComparer.Ordinal), files);
        Assert.Equal(files, await node.FilesAsync(node.RemotePath(Alpha)));
        Assert.Equal(ChainOutcome.Verified, Verify(node, await node.ProjectAsync(Alpha), await node.LinesAsync(clone)).Outcome);
    }

    [Fact]
    public async Task Every_clone_of_a_project_with_more_than_one_repo_carries_the_whole_chain_and_pushes_it()
    {
        const string Gamma = "gamma";
        await using LedgerNode node = await LedgerNode.StartAsync();
        string[] remotes = [Path.Combine(node.Home.Path, "remotes", Gamma, "one.git"), Path.Combine(node.Home.Path, "remotes", Gamma, "two.git")];
        Project gamma = await node.AddProjectAsync(Gamma, remotes);
        Assert.Equal(["one", "two"], gamma.Repos.Select(r => r.Name));

        LedgerPublication published = await node.Export.PublishAsync(Gamma, None);

        Assert.True(published.Published, string.Join("; ", published.Failures));
        int events = (await node.EventsAsync(Gamma)).Count;
        Assert.Equal(events, published.Written);
        IReadOnlyDictionary<string, string> files = await node.FilesAsync(node.ClonePath(Gamma, "one"));
        Assert.NotEmpty(files);
        Assert.Equal(files, await node.FilesAsync(node.ClonePath(Gamma, "two")));
        foreach (string remote in remotes)
        {
            Assert.Equal(files, await node.FilesAsync(remote));
        }

        ChainVerification verified = Verify(node, gamma, await node.LinesAsync(remotes[1]));
        Assert.Equal((ChainOutcome.Verified, events), (verified.Outcome, verified.VerifiedLines));
    }

    [Fact]
    public async Task A_round_that_exports_no_line_binds_no_node_id_so_a_replaced_identity_still_exports()
    {
        await using LedgerNode node = await LedgerNode.StartAsync();

        Assert.Empty(await node.Export.ExportAsync(None));

        await using (IQuerySession session = node.Store.QuerySession(Tenants.Node))
        {
            Assert.Null(await session.LoadAsync<LedgerOrigin>(LedgerOrigin.DocumentId, None));
        }

        await node.AddProjectAsync(Alpha);
        NodeIdentity replaced = await LedgerNode.OtherIdentityAsync();
        using LedgerExport export = node.ExportAs(replaced);
        LedgerPublication published = await export.PublishAsync(Alpha, None);

        Assert.True(published.Published, string.Join("; ", published.Failures));
        await using (IQuerySession session = node.Store.QuerySession(Tenants.Node))
        {
            Assert.Equal(replaced.NodeId, (await session.LoadAsync<LedgerOrigin>(LedgerOrigin.DocumentId, None))?.NodeId);
        }
    }

    [Fact]
    public async Task A_failed_push_is_reported_and_logged_leaves_the_store_and_ref_for_the_next_round_and_stops_no_other_project()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        await node.AddProjectAsync(Beta, withRemote: false);

        IReadOnlyList<LedgerPublication> round = await node.Export.ExportAsync(None);

        LedgerPublication alpha = Assert.Single(round, p => p.Project == Alpha);
        LedgerPublication beta = Assert.Single(round, p => p.Project == Beta);
        Assert.True(alpha.Published, string.Join("; ", alpha.Failures));
        Assert.False(beta.Published);
        string reason = Assert.Single(beta.Failures);
        Assert.StartsWith("The ledger of project 'beta' was not written to or pushed from repo 'repo': ", reason, StringComparison.Ordinal);
        Assert.Contains(node.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(reason, StringComparison.Ordinal));
        Assert.NotNull(await node.RefAsync(node.ClonePath(Beta)));
        Assert.Equal(beta.Written, (await node.RecordsAsync(Beta)).Count);

        await node.MakeRemoteAsync(Beta);
        LedgerPublication next = await node.Export.PublishAsync(Beta, None);

        Assert.Equal((true, 0), (next.Published, next.Written));
        Assert.Equal(await node.FilesAsync(node.ClonePath(Beta)), await node.FilesAsync(node.RemotePath(Beta)));
    }

    [Fact]
    public async Task A_remote_holding_a_line_this_node_does_not_is_never_pushed_over()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        string remote = node.RemotePath(Alpha);
        await node.Export.PublishAsync(Alpha, None);
        string forged = await ForgeAsync(node, remote);

        await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "Not pushed", null, None);
        LedgerPublication published = await node.Export.PublishAsync(Alpha, None);

        Assert.False(published.Published);
        Assert.Contains("the push would remove or change a line the remote holds", Assert.Single(published.Failures), StringComparison.Ordinal);
        Assert.Equal(forged, await node.RefAsync(remote));
    }

    [Fact]
    public async Task A_node_whose_id_is_not_the_one_the_store_exported_under_writes_and_pushes_nothing()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha);
        await node.Export.PublishAsync(Alpha, None);
        string clone = node.ClonePath(Alpha);
        (string? Clone, string? Remote, int Records) before = (await node.RefAsync(clone), await node.RefAsync(node.RemotePath(Alpha)), (await node.RecordsAsync(Alpha)).Count);
        await Parcels(node).AddAsync(Principal.LocalOwner, Alpha, "task", "After the identity changed", null, None);
        NodeIdentity replaced = await LedgerNode.OtherIdentityAsync();
        LedgerExport export = node.ExportAs(replaced);

        IReadOnlyList<LedgerPublication> round = await export.ExportAsync(None);
        LedgerPublication published = await export.PublishAsync(Alpha, None);

        string refused = $"This store exported its lines as node {node.Identity.NodeId}, and this node is {replaced.NodeId}";
        Assert.All([.. round, published], p => Assert.StartsWith(refused, Assert.Single(p.Failures), StringComparison.Ordinal));
        Assert.Equal(before, (await node.RefAsync(clone), await node.RefAsync(node.RemotePath(Alpha)), (await node.RecordsAsync(Alpha)).Count));
        Assert.Equal("", await LedgerNode.GitAsync(clone, "for-each-ref", $"refs/coanda/nodes/{replaced.NodeId:D}"));
        Assert.Contains(node.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(refused, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fork_base_no_module_definition_here_matches_stops_that_projects_export_before_its_line_and_others_still_export()
    {
        await using LedgerNode node = await LedgerNode.StartAsync(Alpha, Beta);
        Guid draftId = Ids.New();
        await using (IDocumentSession session = node.Store.LightweightSession(Alpha))
        {
            Flow fork = SoftwareFlows.Build with { Budget = 40m };
            session.Events.StartStream<Definition>(draftId, new DefinitionDrafted(
                draftId, DefinitionKind.Flow, "build", DefinitionScope.Project, Alpha, 1, Flow.CanonicalJson(fork), fork.Hash,
                ForkBase.Earlier("build", 99), Principal.LocalOwner.Id, DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(None);
        }

        IReadOnlyList<LedgerPublication> round = await node.Export.ExportAsync(None);

        int before = (await node.EventsAsync(Alpha)).Count - 1;
        LedgerPublication alpha = Assert.Single(round, p => p.Project == Alpha);
        string stopped = Assert.Single(alpha.Failures);
        Assert.Equal(
            $"Project 'alpha' stopped exporting before its line {before + 1}: Flow 'build' version 1 was forked from 'build' version 99, a fork base in the earlier shape, and this node holds module-scope Flow 'build' only at version 1, so the base cannot be named by its module and content hash.",
            stopped);
        Assert.Contains(node.Logs.Entries, e => e.Level == LogLevel.Error && e.Message == stopped);
        Assert.Equal(before, (await node.ParsedAsync(node.RemotePath(Alpha))).Count);
        Guid draftEvent = (await node.EventsAsync(Alpha)).Single(e => e.StreamId == draftId).Id;
        Assert.DoesNotContain(await node.RecordsAsync(Alpha), r => r.Id == draftEvent);
        LedgerPublication beta = Assert.Single(round, p => p.Project == Beta);
        Assert.True(beta.Published, string.Join("; ", beta.Failures));
        Assert.Equal((await node.EventsAsync(Beta)).Count, (await node.ParsedAsync(node.RemotePath(Beta))).Count);
    }

    [Fact]
    public async Task While_the_daemon_runs_its_export_loop_pushes_an_event_recorded_in_a_project_to_the_nodes_ref_on_the_remote()
    {
        using var temp = new TemporaryHome();
        string remote = Path.Combine(temp.Home.Path, "remotes", Alpha, $"{RegisteredProjects.Repo}.git");
        Directory.CreateDirectory(remote);
        await LedgerNode.GitAsync(remote, "init", "--bare", "--quiet");
        TimeSpan interval = TimeSpan.FromMilliseconds(250);
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { CommandRunner = new ProcessCommandRunner(), ExportInterval = interval }, None);
        await daemon.Services.GetRequiredService<ProjectService>().AddAsync(Principal.LocalOwner, Alpha, remote, RegisteredProjects.Settings, None);
        NodeIdentity identity = daemon.Services.GetRequiredService<NodeIdentity>();
        string refName = $"refs/coanda/nodes/{identity.NodeId:D}";

        Parcel parcel = await daemon.Services.GetRequiredService<ParcelService>().AddAsync(Principal.LocalOwner, Alpha, "task", "Exported by the loop", null, None);

        // Generous against a loaded machine: the loop itself comes round every quarter of a second.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string? text = null;
        while (DateTimeOffset.UtcNow < deadline && text?.Contains(parcel.Id.ToString("D"), StringComparison.Ordinal) != true)
        {
            await Task.Delay(interval, None);
            text = await RemoteTextAsync(remote, refName);
        }

        Assert.Contains($"\"stream\":\"{parcel.Id:D}\"", text, StringComparison.Ordinal);
        await daemon.StopAsync(None);
        foreach (FileInfo file in new DirectoryInfo(temp.Home.Path).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    /// <summary>Every file the ref holds on the remote, one after another, or null while the remote holds no such ref.</summary>
    private static async Task<string?> RemoteTextAsync(string remote, string refName)
    {
        CommandResult listed = await new ProcessCommandRunner().RunAsync(Git.Against(remote, "ls-tree", "--full-tree", "--name-only", refName), None);
        if (!listed.Succeeded)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (string name in listed.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            text.Append(await LedgerNode.GitAsync(remote, "cat-file", "blob", $"{refName}:{name}"));
        }

        return text.ToString();
    }

    /// <summary>
    /// Points the remote's copy of the node's ref at a commit whose one file holds the remote's lines and one
    /// more that this node never wrote, and gives that commit.
    /// </summary>
    private static async Task<string> ForgeAsync(LedgerNode node, string remote)
    {
        (string name, string text) = Assert.Single(await node.FilesAsync(remote));
        string scratch = Path.Combine(node.Home.Path, "forged");
        Directory.CreateDirectory(scratch);
        string file = Path.Combine(scratch, name);
        await File.WriteAllTextAsync(file, text + "{\"forged\":true}\n", None);
        string blob = (await LedgerNode.GitAsync(remote, "hash-object", "-w", "--no-filters", file)).Trim();
        var index = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = Path.Combine(scratch, "index") };
        await LedgerNode.GitAsync(remote, index, "update-index", "--add", "--cacheinfo", $"100644,{blob},{name}");
        string tree = (await LedgerNode.GitAsync(remote, index, "write-tree")).Trim();
        var author = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "forger", ["GIT_AUTHOR_EMAIL"] = "forger@example.com",
            ["GIT_COMMITTER_NAME"] = "forger", ["GIT_COMMITTER_EMAIL"] = "forger@example.com",
        };
        string commit = (await LedgerNode.GitAsync(remote, author, "commit-tree", tree, "-m", "forged")).Trim();
        await LedgerNode.GitAsync(remote, "update-ref", node.RefName, commit);
        return commit;
    }
}
