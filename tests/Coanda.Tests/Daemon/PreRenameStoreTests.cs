using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Store;
using Coanda.Tests.Daemon.FlowRuns;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

public sealed class PreRenameStoreTests
{
    /// <summary>A parcel type as a build from 2026-11-03 until the stage name drafted it.</summary>
    private const string PreStageType = """{"kind":"ParcelType","name":"task","content":"{\"Name\":\"task\",\"States\":[\"refine\",\"paused\",\"closed\"],\"PausedState\":\"paused\"}"}""";

    /// <summary>A parcel type as this build drafts it.</summary>
    private const string StagedType = """{"kind":"ParcelType","name":"task","content":"{\"Name\":\"task\",\"Stages\":[\"refine\",\"paused\",\"closed\"],\"PausedStage\":\"paused\"}"}""";

    private const string Refusal = "*written by a build from before the stage and step names*Move the node home aside*.pre-stage'*";

    /// <summary>Writes a record holding only the given drafted definitions, the way an older build would have left them.</summary>
    private static void WriteRecord(NodeHome home, params string[] drafted) =>
        WriteEvents(home, [.. drafted.Select(data => ("definition_drafted", data))]);

    /// <summary>Writes a record holding only the given events, each a recorded type name and its data.</summary>
    private static void WriteEvents(NodeHome home, params (string Type, string Data)[] events)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = home.DatabasePath, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE fi_events (seq_id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL, data TEXT NOT NULL)";
        create.ExecuteNonQuery();
        foreach ((string type, string data) in events)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO fi_events (type, data) VALUES ($type, $data)";
            insert.Parameters.AddWithValue("$type", type);
            insert.Parameters.AddWithValue("$data", data);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>The type names of every event in the home's record, in the order recorded.</summary>
    private static List<string> RecordedTypes(NodeHome home)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = home.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT type FROM fi_events ORDER BY seq_id";
        using SqliteDataReader reader = select.ExecuteReader();
        List<string> types = [];
        while (reader.Read())
        {
            types.Add(reader.GetString(0));
        }

        return types;
    }

    [Fact]
    public void A_record_whose_flows_were_drafted_under_the_retired_kind_contour_is_refused()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, PreStageType, """{"kind":"Contour","name":"build","content":"{}"}""");

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        DomainException exception = Assert.Throws<DomainException>(check);
        Assert.Matches(WildcardPattern.ToRegex(Refusal), exception.Message);
    }

    [Fact]
    public void A_record_whose_parcel_type_holds_neither_a_paused_stage_nor_a_paused_state_is_refused()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, """{"kind":"ParcelType","name":"task","content":"{\"Name\":\"task\"}"}""");

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        DomainException exception = Assert.Throws<DomainException>(check);
        Assert.Matches(WildcardPattern.ToRegex(Refusal), exception.Message);
    }

    [Fact]
    public void A_record_whose_parcel_type_holds_states_and_a_paused_state_is_refused()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, PreStageType, """{"kind":"Flow","name":"build","content":"{}"}""");

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        DomainException exception = Assert.Throws<DomainException>(check);
        Assert.Matches(WildcardPattern.ToRegex(Refusal), exception.Message);
    }

    [Fact]
    public void A_record_of_this_builds_kinds_and_shapes_passes()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, StagedType, """{"kind":"Flow","name":"build","content":"{}"}""");

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        check();
    }

    [Fact]
    public void A_home_with_no_record_yet_passes()
    {
        using var temp = new TemporaryHome();

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        check();
    }

    [Fact]
    public async Task A_record_this_build_wrote_passes()
    {
        using var temp = new TemporaryHome();
        await using (RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None))
        {
            using var client = new DaemonClient(temp.Home);
            await client.StopAsync(CancellationToken.None);
            await daemon.WaitForShutdownAsync(CancellationToken.None);
        }

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        check();
    }

    [Fact]
    public async Task The_daemon_refuses_a_record_whose_flows_were_drafted_under_the_retired_kind_contour_and_leaves_it_as_it_was()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, PreStageType, """{"kind":"Contour","name":"build","content":"{}"}""");
        byte[] before = await File.ReadAllBytesAsync(temp.Home.DatabasePath, CancellationToken.None);

        Func<Task> start = () => Daemons.StartAsync(temp.Home, CancellationToken.None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(start);
        Assert.Matches(WildcardPattern.ToRegex(Refusal), exception.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(temp.Home.DatabasePath, CancellationToken.None));
    }

    [Fact]
    public async Task The_daemon_refuses_a_record_from_before_the_stage_and_step_names_and_leaves_it_as_it_was()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, PreStageType, """{"kind":"Flow","name":"build","content":"{}"}""");
        byte[] before = await File.ReadAllBytesAsync(temp.Home.DatabasePath, CancellationToken.None);

        Func<Task> start = () => Daemons.StartAsync(temp.Home, CancellationToken.None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(start);
        Assert.Matches(WildcardPattern.ToRegex(Refusal), exception.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(temp.Home.DatabasePath, CancellationToken.None));
    }

    [Theory]
    [InlineData("flow_started")]
    [InlineData("flow_paused")]
    [InlineData("flow_resumed")]
    [InlineData("flow_ended")]
    [InlineData("flow_stopped")]
    [InlineData("parcel_flow_attached")]
    [InlineData("parcel_flow_released")]
    public void A_record_holding_an_event_a_flow_run_recorded_before_the_flow_run_name_is_refused(string type)
    {
        using var temp = new TemporaryHome();
        WriteEvents(temp.Home, ("definition_drafted", PreStageType), (type, """{"flowId":"0192a3b4-0000-7000-8000-000000000000"}"""));

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        DomainException exception = Assert.Throws<DomainException>(check);
        Assert.Matches(WildcardPattern.ToRegex(Refusal), exception.Message);
    }

    [Fact]
    public async Task The_daemon_refuses_a_record_holding_a_flow_run_from_before_the_flow_run_name_and_leaves_it_as_it_was()
    {
        using var temp = new TemporaryHome();
        WriteEvents(temp.Home, ("definition_drafted", PreStageType), ("flow_started", """{"flowId":"0192a3b4-0000-7000-8000-000000000000"}"""));
        byte[] before = await File.ReadAllBytesAsync(temp.Home.DatabasePath, CancellationToken.None);

        Func<Task> start = () => Daemons.StartAsync(temp.Home, CancellationToken.None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(start);
        Assert.Matches(WildcardPattern.ToRegex(Refusal), exception.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(temp.Home.DatabasePath, CancellationToken.None));
    }

    [Fact]
    public async Task A_record_holding_a_flow_run_this_build_started_names_its_start_flow_run_started_and_passes()
    {
        const string project = "alpha";
        using var temp = new TemporaryHome();
        await using (RunningDaemon daemon = await Daemons.StartAsync(temp.Home, CancellationToken.None))
        {
            await RegisteredProjects.AddAsync(daemon, project, CancellationToken.None);
            using var client = new DaemonClient(temp.Home);
            Flow hold = await Publishing.FlowAsync(daemon, project, new Flow
            {
                Name = "hold-for-a-person",
                Accepts = ["task"],
                Outcome = "approved",
                Budget = 1m,
                SpawnCap = 1,
                Steps =
                [
                    new FlowStep
                    {
                        Name = "approve",
                        SystemStep = CoreSystemSteps.WaitForHuman,
                        Arguments = new Dictionary<string, JsonElement> { ["lever"] = FlowValue.Of("Resume to approve it.") },
                        Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
                    },
                ],
            });
            ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(project, "task", "Needs a nod", null), CancellationToken.None);
            FlowRunView started = await FlowRunDriver.StartAsync(client, project, parcel.Id, hold.Name);
            await FlowRunDriver.UntilAsync(client, project, started.Id, "paused");

            await client.StopAsync(CancellationToken.None);
            await daemon.WaitForShutdownAsync(CancellationToken.None);
        }

        List<string> recorded = RecordedTypes(temp.Home);
        Assert.Contains("flow_run_started", recorded);
        Assert.Contains("parcel_flow_run_attached", recorded);
        Assert.Contains("flow_run_paused", recorded);
        Assert.DoesNotContain(recorded, type => type.StartsWith("flow_", StringComparison.Ordinal) && !type.StartsWith("flow_run_", StringComparison.Ordinal));
        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);
        check();
    }
}
