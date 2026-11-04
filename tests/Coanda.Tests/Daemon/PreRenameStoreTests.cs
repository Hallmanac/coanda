using Coanda.Cli;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Store;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

public sealed class PreRenameStoreTests
{
    private const string PausableType = """{"kind":"ParcelType","name":"task","content":"{\"Name\":\"task\",\"PausedState\":\"paused\"}"}""";

    /// <summary>Writes a record holding only the given drafted definitions, the way an older build would have left them.</summary>
    private static void WriteRecord(NodeHome home, params string[] drafted)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = home.DatabasePath, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE fi_events (seq_id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL, data TEXT NOT NULL)";
        create.ExecuteNonQuery();
        foreach (string data in drafted)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO fi_events (type, data) VALUES ('definition_drafted', $data)";
            insert.Parameters.AddWithValue("$data", data);
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void A_record_whose_contours_were_drafted_under_the_old_kind_is_refused()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, PausableType, """{"kind":"Flow","name":"build","content":"{}"}""");

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        check.Should().Throw<DomainException>().WithMessage("*written by a build from before contours, flows, and pauses*Move the node home aside*");
    }

    [Fact]
    public void A_record_whose_parcel_type_has_no_paused_state_is_refused()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, """{"kind":"ParcelType","name":"task","content":"{\"Name\":\"task\"}"}""");

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        check.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_record_of_this_builds_kinds_and_shapes_passes()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, PausableType, """{"kind":"Contour","name":"build","content":"{}"}""");

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        check.Should().NotThrow();
    }

    [Fact]
    public void A_home_with_no_record_yet_passes()
    {
        using var temp = new TemporaryHome();

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        check.Should().NotThrow();
    }

    [Fact]
    public async Task A_record_this_build_wrote_passes()
    {
        using var temp = new TemporaryHome();
        await using (RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, CancellationToken.None))
        {
            using var client = new DaemonClient(temp.Home);
            await client.StopAsync(CancellationToken.None);
            await daemon.WaitForShutdownAsync(CancellationToken.None);
        }

        Action check = () => PreRenameStore.ThrowIfFound(temp.Home);

        check.Should().NotThrow();
    }

    [Fact]
    public async Task The_daemon_refuses_an_older_record_and_leaves_it_as_it_was()
    {
        using var temp = new TemporaryHome();
        WriteRecord(temp.Home, """{"kind":"Flow","name":"build","content":"{}"}""");
        byte[] before = await File.ReadAllBytesAsync(temp.Home.DatabasePath, CancellationToken.None);

        Func<Task> start = () => DaemonHost.StartAsync(temp.Home, 0, CancellationToken.None);

        await start.Should().ThrowAsync<DomainException>();
        (await File.ReadAllBytesAsync(temp.Home.DatabasePath, CancellationToken.None)).Should().Equal(before);
    }
}
