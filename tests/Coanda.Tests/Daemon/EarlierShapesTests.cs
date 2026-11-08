using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Coanda.Tests.Core;
using Fisher;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The daemon's start, the CLI's detached start, and the record export each refuse a home whose record
/// holds an event in a shape an earlier build wrote, with one message that names the commit to rebuild
/// with and the page that gives the steps, and leave the record as it was.
/// </summary>
public sealed class EarlierShapesTests
{
    private const string Project = "alpha";

    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>
    /// Each earlier shape, as one statement that puts it into a home restored from the rebuilt fixture, which
    /// holds current shapes only, and the words the refusal names it by.
    /// </summary>
    public static TheoryData<string, string, string> Shapes => new()
    {
        {
            "shipped-scope",
            "UPDATE fi_events SET data = json_remove(json_set(data, '$.scope', 'Shipped'), '$.module', '$.moduleVersion') WHERE seq_id = (SELECT min(seq_id) FROM fi_events WHERE type = 'definition_drafted' AND json_extract(data, '$.scope') = 'Module')",
            "in the scope stored as 'Shipped'"
        },
        {
            "unprefixed-start-kind-number",
            "UPDATE fi_events SET data = json_set(data, '$.firstStepKind', 1, '$.firstStepPerformer', 'worktree') WHERE type = 'flow_run_started'",
            "names the system step 'worktree' without the 'software/' prefix"
        },
        {
            "unprefixed-start-kind-name",
            "UPDATE fi_events SET data = json_set(data, '$.firstStepKind', 'SystemStep', '$.firstStepPerformer', 'worktree') WHERE type = 'flow_run_started'",
            "names the system step 'worktree' without the 'software/' prefix"
        },
        {
            "unprefixed-step-started",
            "UPDATE fi_events SET data = json_set(data, '$.performer', 'gates') WHERE type = 'step_started'",
            "its 'step_started' names the system step 'gates'"
        },
        {
            "unprefixed-dispatch",
            "UPDATE fi_events SET data = json_set(data, '$.systemStep', 'gates') WHERE type = 'system_step_dispatched' AND json_extract(data, '$.step') = 'gates'",
            "its 'system_step_dispatched' names the system step 'gates'"
        },
        {
            "unprefixed-completion",
            "UPDATE fi_events SET data = json_set(data, '$.systemStep', 'worktree') WHERE type = 'system_step_completed'",
            "its 'system_step_completed' names the system step 'worktree'"
        },
        {
            "registration-remote-without-repos",
            "UPDATE fi_events SET data = json_set(json_remove(data, '$.repos'), '$.remote', 'https://github.com/example/repo.git') WHERE type = 'project_registered'",
            "carries 'remote'"
        },
        {
            "registration-primary-branch-with-repos",
            "UPDATE fi_events SET data = json_set(data, '$.primaryBranch', 'main') WHERE type = 'project_registered'",
            "carries 'primaryBranch'"
        },
        {
            "registration-build-command",
            "UPDATE fi_events SET data = json_set(data, '$.buildCommand', 'make build') WHERE type = 'project_registered'",
            "carries 'buildCommand'"
        },
        {
            "registration-test-command",
            "UPDATE fi_events SET data = json_set(data, '$.testCommand', 'make test') WHERE type = 'project_registered'",
            "carries 'testCommand'"
        },
        {
            "required-checks",
            "UPDATE fi_events SET type = 'project_required_checks_set', data = '{\"checks\":[\"build\"]}' WHERE seq_id = (SELECT max(seq_id) FROM fi_events WHERE type = 'module_setting_set')",
            "'project_required_checks_set'"
        },
        {
            "fork-base-without-content-hash",
            "UPDATE fi_events SET data = json_set(data, '$.forkBase', json('{\"name\":\"build\",\"version\":2}')) WHERE type = 'definition_drafted' AND json_extract(data, '$.forkBase.name') = 'build'",
            "fork base of a name and a store-given version"
        },
    };

    public static TheoryData<string> EarlierFixtures => new([RecordHomes.WrittenBy1fed907, RecordHomes.WrittenByE86428f]);

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_home_holding_an_earlier_shape_is_refused_by_the_daemon_the_detached_start_and_the_export_alike(string shape, string statement, string named)
    {
        using var temp = new TemporaryHome();
        RecordHomes.Restore(temp.Home, RecordHomes.RebuiltByEe75e50);
        using (SqliteConnection connection = RecordHomes.Open(temp.Home.DatabasePath))
        using (SqliteCommand earlier = connection.CreateCommand())
        {
            earlier.CommandText = statement;
            Assert.True(earlier.ExecuteNonQuery() > 0, $"{shape} changed no event");
        }

        string refusal = await AssertRefusedAsync(temp.Home);

        Assert.Contains(named, refusal, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EarlierFixtures))]
    public async Task A_home_an_earlier_build_wrote_is_refused_by_the_daemon_the_detached_start_and_the_export_alike(string fixture)
    {
        using var temp = new TemporaryHome();
        RecordHomes.Restore(temp.Home, fixture);

        await AssertRefusedAsync(temp.Home);
    }

    [Fact]
    public async Task A_home_this_build_wrote_holding_a_flow_run_an_earlier_build_recorded_unprefixed_is_refused()
    {
        using var temp = new TemporaryHome();
        string database = temp.Home.EnsureExists().EnsureDatabase().DatabasePath;
        await using (IDocumentStore store = await SeededStore.OpenAsync(database, SoftwareDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, Project, None);
            await PreviousFlows.PublishAsync(store, Project, PreviousFlows.Build);
            Parcel parcel = await new ParcelService(store, TimeProvider.System).AddAsync(Principal.LocalOwner, Project, "task", "Dispatched before the carve-out", null, None);
            await EarlierFlowRuns.DispatchedAsync(store, Project, parcel.Id, None);
        }

        SqliteConnection.ClearAllPools();
        string refusal = await AssertRefusedAsync(temp.Home);

        Assert.Contains("its 'flow_run_started' names the system step 'worktree' without the 'software/' prefix", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rebuilt_home_is_not_refused()
    {
        using var temp = new TemporaryHome();
        RecordHomes.Restore(temp.Home, RecordHomes.RebuiltByEe75e50);

        EarlierShapes.ThrowIfFound(temp.Home);
        using (var client = new DaemonClient(temp.Home))
        {
            client.PrepareToStart();
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await daemon.StopAsync(None);
    }

    [Fact]
    public void A_home_with_no_record_or_no_events_table_is_not_refused()
    {
        using var temp = new TemporaryHome();
        EarlierShapes.ThrowIfFound(temp.Home);

        using (SqliteConnection connection = RecordHomes.Open(temp.Home.DatabasePath))
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE unrelated (id INTEGER)";
            create.ExecuteNonQuery();
        }

        EarlierShapes.ThrowIfFound(temp.Home);
    }

    /// <summary>
    /// Asserts the daemon's start, the detached start's check, and the export each refuse the home with one
    /// message, naming the commit and the rebuild page, and that the record's files are as they were. Gives
    /// the message.
    /// </summary>
    private static async Task<string> AssertRefusedAsync(NodeHome home)
    {
        IReadOnlyDictionary<string, string> before = RecordFiles(home);

        DomainException started = await Assert.ThrowsAsync<DomainException>(() => Daemons.StartAsync(home, None));
        using var client = new DaemonClient(home);
        DomainException detached = Assert.Throws<DomainException>(client.PrepareToStart);
        DomainException exported = await Assert.ThrowsAsync<DomainException>(() => RecordExport.WriteAsync(home, TimeProvider.System, None));

        Assert.Equal(started.Message, detached.Message);
        Assert.Equal(started.Message, exported.Message);
        Assert.Contains(EarlierShapes.RebuildCommit, started.Message, StringComparison.Ordinal);
        Assert.Contains("docs/rebuilding-the-record.md", started.Message, StringComparison.Ordinal);
        Assert.Equal(before, RecordFiles(home));
        Assert.False(Directory.Exists(home.ExportsPath) && Directory.EnumerateFileSystemEntries(home.ExportsPath).Any());
        return started.Message;
    }

    /// <summary>The record's database and whichever companion files it has, by name, each with a hash of its content.</summary>
    private static IReadOnlyDictionary<string, string> RecordFiles(NodeHome home)
    {
        HashSet<string> record = [.. ((IEnumerable<string>)[home.DatabasePath, .. home.DatabaseCompanionPaths]).Select(p => Path.GetRelativePath(home.Path, p))];
        return RecordHomes.Files(home.Path).Where(f => record.Contains(f.Key)).ToDictionary(StringComparer.Ordinal);
    }
}
