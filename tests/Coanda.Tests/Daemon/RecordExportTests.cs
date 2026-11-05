using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;
using Coanda.Daemon.Triggers;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Fisher;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A node writes a full export of its record, every event in its current shape, to an owner-only file
/// in its home, and rebuilds its record from that export so that it reads as the same home reads after a
/// start of this build; both refuse, changing nothing, while a daemon runs on the home or is starting.
/// </summary>
public sealed class RecordExportTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Task<RecordExportResult> ExportAsync(NodeHome home) => RecordExport.WriteAsync(home, TimeProvider.System, None);

    private static Task<RecordRebuildResult> RebuildAsync(NodeHome home, string export) => RecordRebuild.RunAsync(home, export, TimeProvider.System, None);

    private static async Task<RecordExportFile> ReadAsync(string path) => await RecordExportFile.ReadAsync(path, None);

    [Theory]
    [MemberData(nameof(RecordHomes.All), MemberType = typeof(RecordHomes))]
    public async Task A_home_reads_the_same_after_an_export_and_a_rebuild_as_after_a_start_and_keeps_every_events_id_stream_tenant_and_time(string which)
    {
        using var started = new TemporaryHome();
        await RecordHomes.WriteAsync(started.Home, which);
        IReadOnlyList<string> expected;
        await using (RunningDaemon daemon = await Daemons.StartAsync(started.Home, None))
        {
            expected = await RecordHomes.ReadAsync(daemon);
            await daemon.StopAsync(None);
        }

        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        await RecordHomes.WriteAsync(home, which);
        RecordExportResult export = await ExportAsync(home);
        IReadOnlyList<(string Id, string Stream, string Tenant, string Timestamp, string Type, string Data)> held = RecordHomes.Events(home.DatabasePath);
        await RebuildAsync(home, export.Path);

        // Only the checks event is left out, and every other keeps its id, stream, tenant, and time, in order.
        Assert.Equal(
            held.Where(e => e.Type != "project_required_checks_set").Select(e => (e.Id, e.Stream, e.Tenant, e.Timestamp)),
            RecordHomes.Events(home.DatabasePath).Select(e => (e.Id, e.Stream, e.Tenant, e.Timestamp)));

        IReadOnlyList<string> actual;
        await using (RunningDaemon daemon = await Daemons.StartAsync(home, None))
        {
            actual = await RecordHomes.ReadAsync(daemon);
            await daemon.StopAsync(None);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task An_earlier_project_record_reaches_the_rebuilt_record_from_a_home_this_build_never_started()
    {
        using var temp = new TemporaryHome();
        RecordHomes.Restore(temp.Home, RecordHomes.WrittenBy1fed907);
        RecordExportResult export = await ExportAsync(temp.Home);
        await RebuildAsync(temp.Home, export.Path);

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        string alpha = Assert.Single(await RecordHomes.ReadAsync(daemon), r => r.StartsWith("settings ", StringComparison.Ordinal) && r.Contains("\"Project\":\"alpha\"", StringComparison.Ordinal));
        Assert.Contains("trunk", alpha, StringComparison.Ordinal);
        Assert.Contains("make build", alpha, StringComparison.Ordinal);
        Assert.Contains("make test", alpha, StringComparison.Ordinal);
        Assert.Contains("lint", alpha, StringComparison.Ordinal);
        await daemon.StopAsync(None);
    }

    [Theory]
    [MemberData(nameof(RecordHomes.All), MemberType = typeof(RecordHomes))]
    public async Task The_rebuilt_record_holds_no_earlier_shape_but_the_two_that_stay_current(string which)
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        await RecordHomes.WriteAsync(home, which);
        RecordExportResult export = await ExportAsync(home);
        Dictionary<string, (string? Content, string? Hash)> heldContent = RecordHomes.Events(home.DatabasePath)
            .Where(e => e.Type == "definition_drafted")
            .ToDictionary(e => e.Id, e => Content(e.Data), StringComparer.Ordinal);
        await RebuildAsync(home, export.Path);

        IReadOnlyList<(string Id, string Stream, string Tenant, string Timestamp, string Type, string Data)> rows = RecordHomes.Events(home.DatabasePath);
        Assert.DoesNotContain(rows, r => r.Type == "project_required_checks_set");
        foreach ((string id, _, _, _, string type, string data) in rows)
        {
            JsonObject json = JsonNode.Parse(data)?.AsObject() ?? throw new InvalidOperationException("An event with no data.");
            switch (type)
            {
                case "definition_drafted":
                    Assert.NotEqual("Shipped", json["scope"]?.GetValue<string>());
                    Assert.True(json["scope"]?.GetValue<string>() != "Module" || json["module"] is not null, $"{id} is in module scope with no module");
                    if (json["forkBase"] is JsonObject forkBase)
                    {
                        Assert.Null(forkBase["version"]);
                        Assert.NotNull(forkBase["contentHash"]);
                        Assert.NotNull(forkBase["module"]);
                    }

                    // Content, unprefixed step names included, and its hash are as stored.
                    Assert.Equal(heldContent[id], Content(data));
                    break;
                case "project_registered":
                    Assert.NotNull(json["repos"]);
                    Assert.All(["remote", "primaryBranch", "buildCommand", "testCommand"], member => Assert.False(json.ContainsKey(member), $"{id} still records {member}"));
                    break;
            }

            string? step = EarlierFlowRuns.SystemStepOf(new StoredRow(0, string.Empty, 0, type, data, string.Empty, 0));
            Assert.True(step is null || step.Contains('/') || !EarlierRecords.UnprefixedSteps.Contains(step), $"{type} {id} names {step} unprefixed");
        }

        // The documents regenerated from the events hold no earlier shape either.
        Assert.All(RecordHomes.Documents(home.DatabasePath, "fi_doc_definition"), d => Assert.DoesNotContain("\"Shipped\"", d.Data, StringComparison.Ordinal));
        Assert.All(RecordHomes.Documents(home.DatabasePath, "fi_doc_project"), d => Assert.DoesNotContain("primaryBranch", d.Data, StringComparison.Ordinal));
        foreach ((_, _, string data) in RecordHomes.Documents(home.DatabasePath, "fi_doc_flowrun"))
        {
            IEnumerable<string?> performers = (JsonNode.Parse(data)?["steps"]?.AsArray() ?? []).Select(v => v?["performer"]?.GetValue<string>());
            Assert.All(performers, p => Assert.DoesNotContain(p, EarlierRecords.UnprefixedSteps));
        }

        if (which == RecordHomes.Extended)
        {
            // The flow from before the build flow ended at the open pull request still routes the unprefixed open-pr on done alone.
            (_, _, _, _, _, string gated) = Assert.Single(rows, r => r.Type == "definition_drafted" && JsonNode.Parse(r.Data)?["name"]?.GetValue<string>() == RecordHomes.GatedBuild.Name);
            string content = JsonNode.Parse(gated)?["content"]?.GetValue<string>() ?? "{}";
            FlowStep openPr = Assert.Single(Flow.FromJson(content).Steps, s => s.Name == "open-pr");
            JsonNode? stored = (JsonNode.Parse(content)?["Steps"]?.AsArray() ?? []).Single(s => s?["Name"]?.GetValue<string>() == "open-pr");
            Assert.Equal("open-pr", stored?["SystemStep"]?.GetValue<string>());
            Assert.Equal(RecordHomes.GatedBuild.Hash, JsonNode.Parse(gated)?["contentHash"]?.GetValue<string>());
            Assert.Equal(["done"], openPr.Transitions.Select(t => t.Value.GetString()));

            JsonObject follows = JsonNode.Parse(Assert.Single(RecordHomes.Documents(home.DatabasePath, "fi_doc_deferredfollows")).Data)?.AsObject() ?? throw new InvalidOperationException("No deferred follows.");
            Assert.False(Assert.Single(follows["pending"]?.AsArray() ?? []) ?["closes"]?.GetValue<bool>() ?? true);
        }
    }

    [Fact]
    public void The_function_writes_an_agent_step_spawned_with_null_members_with_those_members_null()
    {
        var spawned = new StoredEvent(
            "agent_step_spawned",
            """{"flowRunId":"01a1aa2b-396f-76c0-b4f7-561db6135f30","step":"build","agent":"build","agentHash":null,"model":null,"spawnedAt":"2026-11-03T06:00:19.192743+00:00","launchId":null}""");

        Assert.Equal(spawned, EarlierRecords.Exported(spawned, (_, _, _) => throw new InvalidOperationException("Nothing to resolve.")));
    }

    [Fact]
    public async Task The_export_is_owner_only_names_the_last_event_and_holds_every_tenant_and_every_document_but_the_inline_snapshots()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        await RecordHomes.WriteAsync(home, RecordHomes.Extended);
        await using (RunningDaemon daemon = await Daemons.StartAsync(home, None))
        {
            await daemon.StopAsync(None);
        }

        RecordExportResult result = await ExportAsync(home);
        RecordHomes.AssertOwnerOnly(result.Path);
        Assert.StartsWith(home.ExportsPath, result.Path, StringComparison.Ordinal);
        RecordExportFile export = await ReadAsync(result.Path);

        IReadOnlyList<string> tables;
        ExportedEventReference? last;
        using (SqliteConnection connection = RecordHomes.Open(home.DatabasePath, SqliteOpenMode.ReadOnly))
        {
            tables = RecordExport.AllDocumentTables(connection);
            last = RecordExport.LastEvent(connection);
        }

        HashSet<string> inline = [.. StoreConfiguration.InlineSnapshots.Select(s => StoreConfiguration.DocumentTableOf(s.Type))];
        Assert.Equal(tables.Where(t => !inline.Contains(t)), export.Documents.Select(d => d.Table));
        Assert.Subset(
            tables.ToHashSet(),
            new HashSet<string>(["fi_doc_triggerposition", "fi_doc_feedposition", "fi_doc_deferredfollows", "fi_doc_sessiontokenrecord", "fi_doc_moduletokenrecord"]));
        Assert.All(new[] { typeof(TriggerPosition), typeof(FeedPosition), typeof(DeferredFollows), typeof(ModuleTokenRecord), typeof(Coanda.Daemon.FlowRuns.SessionTokenRecord) },
            type => Assert.Contains(StoreConfiguration.DocumentTableOf(type), export.Documents.Select(d => d.Table)));
        Assert.All(inline, table => Assert.Contains(table, tables));

        Assert.Equal(last, export.LastEvent);
        Assert.Equal([Tenants.Node, "alpha"], export.Events.Select(e => e.Tenant).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(RecordHomes.Events(home.DatabasePath).Count(e => e.Type != "project_required_checks_set"), export.Events.Count);
    }

    [Fact]
    public async Task Each_loop_position_names_the_event_it_named_and_the_parcel_past_the_trigger_position_stays_past_it()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.WrittenByE86428f);
        RecordExportResult export = await ExportAsync(home);
        IReadOnlyList<(string Id, string Stream, string Tenant, string Timestamp, string Type, string Data)> held = RecordHomes.Events(home.DatabasePath);
        await RebuildAsync(home, export.Path);
        IReadOnlyList<(string Id, string Stream, string Tenant, string Timestamp, string Type, string Data)> rebuilt = RecordHomes.Events(home.DatabasePath);

        // The trigger position named the 31st event, which is kept; the 27th, the checks event, is left out, so it is the 30th now.
        long position = TriggerPositionOf(home);
        Assert.Equal(30, position);
        Assert.Equal(held[30].Id, rebuilt[(int)position - 1].Id);
        int created = rebuilt.ToList().FindIndex(e => e.Type == "parcel_created") + 1;
        Assert.True(created > position, $"parcel_created is at {created}, at or before the trigger position {position}");
    }

    [Fact]
    public async Task A_loop_position_at_an_event_left_out_names_the_last_exported_event_before_it()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.WrittenByE86428f);
        using (SqliteConnection connection = RecordHomes.Open(home.DatabasePath))
        using (SqliteCommand atChecks = connection.CreateCommand())
        {
            atChecks.CommandText = "UPDATE fi_doc_triggerposition SET data = json_set(data, '$.sequence', 27)";
            Assert.Equal(1, atChecks.ExecuteNonQuery());
        }

        RecordExportResult export = await ExportAsync(home);
        IReadOnlyList<(string Id, string Stream, string Tenant, string Timestamp, string Type, string Data)> held = RecordHomes.Events(home.DatabasePath);
        Assert.Equal("project_required_checks_set", held[26].Type);
        await RebuildAsync(home, export.Path);

        long position = TriggerPositionOf(home);
        Assert.Equal(26, position);
        Assert.Equal(held[25].Id, RecordHomes.Events(home.DatabasePath)[(int)position - 1].Id);
        Assert.Equal(held[27].Id, RecordHomes.Events(home.DatabasePath)[(int)position].Id);
    }

    [Fact]
    public async Task A_ledger_lines_store_sequence_names_the_event_it_named_and_its_chain_sequence_stays_as_it_was()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.WrittenByE86428f);
        IReadOnlyList<(string Id, string Stream, string Tenant, string Timestamp, string Type, string Data)> held = RecordHomes.Events(home.DatabasePath);
        Guid lined = Guid.Parse(held[30].Id);
        await using (IDocumentStore store = StoreConfiguration.Open(home.DatabasePath))
        {
            await store.ApplyAllConfiguredChangesToDatabaseAsync(None);
            await using IDocumentSession session = store.LightweightSession(held[30].Tenant);
            session.Store(new LedgerLine { Id = lined, Origin = Guid.Parse("01a1ad07-0000-7000-8000-000000000001"), Sequence = 7, Signature = "seeded", Hash = "seeded", Segment = "2026-11", Line = "{}", StoreSequence = 31 });
            await session.SaveChangesAsync(None);
        }

        SqliteConnection.ClearAllPools();
        RecordExportResult export = await ExportAsync(home);
        await RebuildAsync(home, export.Path);

        // The 31st event is kept and the 27th, the checks event, is left out, so the line's event is the 30th now.
        JsonNode line = JsonNode.Parse(Assert.Single(RecordHomes.Documents(home.DatabasePath, StoreConfiguration.DocumentTableOf(typeof(LedgerLine)))).Data)
            ?? throw new InvalidOperationException("The line record does not parse.");
        Assert.Equal(30, line["storeSequence"]?.GetValue<long>());
        Assert.Equal(7, line["sequence"]?.GetValue<long>());
        Assert.Equal(held[30].Id, RecordHomes.Events(home.DatabasePath)[29].Id);
    }

    private static long TriggerPositionOf(NodeHome home) =>
        JsonNode.Parse(Assert.Single(RecordHomes.Documents(home.DatabasePath, "fi_doc_triggerposition")).Data)?["sequence"]?.GetValue<long>() ?? -1;

    [Fact]
    public async Task Before_a_start_the_rebuilt_record_holds_the_deferred_follows_and_session_and_module_tokens_it_replaced()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        await RecordHomes.WriteAsync(home, RecordHomes.Extended);
        RecordExportResult export = await ExportAsync(home);
        RecordRebuildResult rebuilt = await RebuildAsync(home, export.Path);
        string replaced = Path.Combine(rebuilt.MovedAside, Path.GetFileName(home.DatabasePath));

        foreach (string table in new[] { "fi_doc_sessiontokenrecord", "fi_doc_moduletokenrecord" })
        {
            IReadOnlyList<(string Tenant, string Id, string Data)> before = RecordHomes.Documents(replaced, table);
            Assert.NotEmpty(before);
            Assert.Equal(before, RecordHomes.Documents(home.DatabasePath, table));
        }

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        DeferredFollows earlier = JsonSerializer.Deserialize<DeferredFollows>(Assert.Single(RecordHomes.Documents(replaced, "fi_doc_deferredfollows")).Data, web) ?? throw new InvalidOperationException();
        DeferredFollows current = JsonSerializer.Deserialize<DeferredFollows>(Assert.Single(RecordHomes.Documents(home.DatabasePath, "fi_doc_deferredfollows")).Data, web) ?? throw new InvalidOperationException();
        Assert.Equal(earlier.Id, current.Id);
        Assert.Equal(earlier.Pending, current.Pending);
    }

    [Fact]
    public async Task A_rebuild_moves_the_record_aside_unchanged_and_owner_only_and_leaves_every_other_file_where_it_was()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        await RecordHomes.WriteAsync(home, RecordHomes.Extended);
        RecordExportResult export = await ExportAsync(home);
        File.WriteAllText(Path.Combine(home.Path, "node.json"), "{}");
        IReadOnlyDictionary<string, string> before = RecordHomes.Files(home.Path);
        string[] record = [.. ((IEnumerable<string>)[home.DatabasePath, .. home.DatabaseCompanionPaths]).Where(File.Exists).Select(p => Path.GetRelativePath(home.Path, p))];

        RecordRebuildResult rebuilt = await RebuildAsync(home, export.Path);

        IReadOnlyDictionary<string, string> after = RecordHomes.Files(home.Path);
        string aside = Path.GetRelativePath(home.Path, rebuilt.MovedAside);
        foreach (string file in record)
        {
            Assert.Equal(before[file], after[Path.Combine(aside, file)]);
            RecordHomes.AssertOwnerOnly(Path.Combine(rebuilt.MovedAside, file));
        }

        Assert.Equal(
            before.Where(f => !record.Contains(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal),
            after.Where(f => f.Key != Path.GetFileName(home.DatabasePath) && !f.Key.StartsWith(aside, StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal));
        RecordHomes.AssertOwnerOnly(home.DatabasePath);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(rebuilt.MovedAside));
        }
    }

    [Fact]
    public async Task An_export_whose_fork_base_resolves_to_nothing_stops_names_the_definition_and_leaves_no_file()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.WrittenByE86428f);
        using (SqliteConnection connection = RecordHomes.Open(home.DatabasePath))
        using (SqliteCommand unresolved = connection.CreateCommand())
        {
            unresolved.CommandText = "UPDATE fi_events SET data = json_set(data, '$.forkBase.version', 9) WHERE seq_id = 28";
            Assert.Equal(1, unresolved.ExecuteNonQuery());
        }

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => ExportAsync(home));
        Assert.Contains("'build'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("project 'alpha'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("version 9", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(home.ExportsPath) && Directory.EnumerateFileSystemEntries(home.ExportsPath).Any());
    }

    [Fact]
    public async Task Neither_runs_while_a_daemon_runs_on_the_home_and_neither_changes_anything()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.WrittenByE86428f);
        RecordExportResult export = await ExportAsync(home);

        await using RunningDaemon daemon = await Daemons.StartAsync(home, None);
        await AssertRefusedAsync(home, export.Path);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Neither_runs_while_a_daemon_is_starting_on_the_home_and_neither_changes_anything()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.WrittenByE86428f);
        RecordExportResult export = await ExportAsync(home);

        var module = new HeldHealthModule();
        Task<RunningDaemon> starting = Daemons.StartAsync(home, new DaemonOptions { Modules = new Dictionary<string, IModule> { ["held"] = module } }, None);
        await module.Asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(File.Exists(home.DaemonInfoPath));
        try
        {
            await AssertRefusedAsync(home, export.Path);
        }
        finally
        {
            module.Answer.SetResult();
            await using RunningDaemon daemon = await starting;
            await daemon.StopAsync(None);
        }
    }

    [Fact]
    public async Task A_daemon_does_not_start_while_an_export_or_rebuild_holds_the_home()
    {
        using var temp = new TemporaryHome();
        using (HomeLock.Take(temp.Home, "a test"))
        {
            DomainException refused = await Assert.ThrowsAsync<DomainException>(() => Daemons.StartAsync(temp.Home, None));
            Assert.Contains("the daemon did not start", refused.Message, StringComparison.Ordinal);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_rebuild_from_an_export_it_cannot_read_changes_nothing()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.WrittenByE86428f);
        RecordExportResult export = await ExportAsync(home);
        string unreadable = Path.Combine(home.ExportsPath, "unreadable.json");
        File.WriteAllText(unreadable, "{ not json");
        IReadOnlyDictionary<string, string> before = RecordHomes.Files(home.Path);

        await Assert.ThrowsAsync<DomainException>(() => RebuildAsync(home, unreadable));
        await Assert.ThrowsAsync<DomainException>(() => RebuildAsync(home, Path.Combine(home.ExportsPath, "missing.json")));
        JsonObject gapped = JsonNode.Parse(File.ReadAllText(export.Path))?.AsObject() ?? throw new InvalidOperationException();
        gapped["events"]?.AsArray().RemoveAt(3);
        string gappedPath = Path.Combine(home.ExportsPath, "gapped.json");
        File.WriteAllText(gappedPath, gapped.ToJsonString());
        before = RecordHomes.Files(home.Path);
        await Assert.ThrowsAsync<DomainException>(() => RebuildAsync(home, gappedPath));

        Assert.Equal(before, RecordHomes.Files(home.Path));
    }

    [Fact]
    public async Task A_rebuild_refuses_when_the_records_last_event_is_not_the_one_the_export_names_and_changes_nothing()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.WrittenByE86428f);
        RecordExportResult export = await ExportAsync(home);
        await using (RunningDaemon daemon = await Daemons.StartAsync(home, None))
        {
            await daemon.StopAsync(None);
        }

        IReadOnlyDictionary<string, string> before = RecordHomes.Files(home.Path);
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => RebuildAsync(home, export.Path));

        Assert.Contains(export.LastEvent?.Id ?? "none", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, RecordHomes.Files(home.Path));
    }

    [Fact]
    public async Task A_rebuild_that_fails_part_way_leaves_the_home_as_it_found_it()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        await RecordHomes.WriteAsync(home, RecordHomes.Extended);
        RecordExportResult export = await ExportAsync(home);
        JsonObject broken = JsonNode.Parse(File.ReadAllText(export.Path))?.AsObject() ?? throw new InvalidOperationException();
        JsonObject table = broken["documents"]?.AsArray().OfType<JsonObject>().First(t => t["table"]?.GetValue<string>() == "fi_doc_deferredfollows") ?? throw new InvalidOperationException();
        table["schema"] = new JsonArray("CREATE TABLE fi_doc_deferredfollows (tenant_id TEXT NOT NULL)");
        string brokenPath = Path.Combine(home.ExportsPath, "broken.json");
        File.WriteAllText(brokenPath, broken.ToJsonString());
        IReadOnlyDictionary<string, string> before = RecordHomes.Files(home.Path);

        await Assert.ThrowsAnyAsync<Exception>(() => RebuildAsync(home, brokenPath));

        Assert.Equal(before, RecordHomes.Files(home.Path));
        Assert.Empty(Directory.EnumerateDirectories(home.Path, "record-before-rebuild-*"));
    }

    /// <summary>
    /// Asserts the export and the rebuild both refuse, and that neither changed any file in the home. The
    /// files the daemon itself writes while it runs (its record and its log) are left out of the
    /// comparison; what an export or a rebuild would have left beside them is looked for instead.
    /// </summary>
    private static async Task AssertRefusedAsync(NodeHome home, string export)
    {
        IReadOnlyDictionary<string, string> before = OutsideTheDaemon(home);
        DomainException exportRefused = await Assert.ThrowsAsync<DomainException>(() => ExportAsync(home));
        DomainException rebuildRefused = await Assert.ThrowsAsync<DomainException>(() => RebuildAsync(home, export));
        Assert.Contains("A daemon is running or starting", exportRefused.Message, StringComparison.Ordinal);
        Assert.Contains("A daemon is running or starting", rebuildRefused.Message, StringComparison.Ordinal);
        Assert.Equal(before, OutsideTheDaemon(home));
        Assert.Empty(Directory.EnumerateFileSystemEntries(home.Path, "record-before-rebuild-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(home.Path, $"{Path.GetFileName(home.DatabasePath)}.rebuild-*"));
    }

    private static IReadOnlyDictionary<string, string> OutsideTheDaemon(NodeHome home)
    {
        HashSet<string> daemons = [.. ((IEnumerable<string>)[home.DatabasePath, .. home.DatabaseCompanionPaths, home.LogPath, home.DaemonOutputPath]).Select(p => Path.GetRelativePath(home.Path, p))];
        return RecordHomes.Files(home.Path, daemons);
    }

    private static (string? Content, string? Hash) Content(string data)
    {
        JsonNode? json = JsonNode.Parse(data);
        return (json?["content"]?.GetValue<string>(), json?["contentHash"]?.GetValue<string>());
    }

    /// <summary>A stand-in module whose health answers only once the test lets it, so the daemon's start is held part way.</summary>
    private sealed class HeldHealthModule : IModule
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HealthAsync(CancellationToken cancellationToken)
        {
            Asked.TrySetResult();
            await Answer.Task.WaitAsync(cancellationToken);
        }

        public Task<DescribeAnswer> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DescribeAnswer(ModuleApi.Version, "1.0.0", [], [], [], new SettingsSchema([])));

        public Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<DispatchAnswer>(new DispatchAnswer.Done(new StepResult("done")));

        public Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<StatusAnswer>(new StatusAnswer.Unknown());

        public Task StopAsync(StopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FeedAsync(FeedDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
