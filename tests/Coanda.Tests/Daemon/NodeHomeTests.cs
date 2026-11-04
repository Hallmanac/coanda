using Coanda.Core;
using Coanda.Daemon;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

public sealed class NodeHomeTests
{
    private static readonly NodeHome Home = new(Path.Combine(Path.GetTempPath(), "coanda-home"));

    [Fact]
    public void A_flow_runs_work_path_sits_under_the_homes_work_directory()
    {
        Guid flowRunId = Ids.New();

        Assert.Equal(Path.Combine(Home.Path, "work", "alpha", flowRunId.ToString()), Home.FlowRunWorkPath("alpha", flowRunId));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../..")]
    [InlineData("/tmp/elsewhere")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_project_that_is_not_a_plain_directory_name_has_no_work_path(string project)
    {
        Action work = () => Home.FlowRunWorkPath(project, Ids.New());
        Action transcripts = () => Home.FlowRunTranscriptPath(project, Ids.New());

        DomainException exception = Assert.Throws<DomainException>(work);
        Assert.StartsWith($"Project '{project}' is not a plain directory name", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(transcripts);
        Assert.StartsWith($"Project '{project}' is not a plain directory name", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_runs_transcripts_sit_under_the_homes_transcript_directory_beside_its_work_directory_never_inside_it()
    {
        Guid flowRunId = Ids.New();
        string work = Home.FlowRunWorkPath("alpha", flowRunId);

        string transcripts = Home.TranscriptDirectoryOf(work);

        Assert.Equal(Path.Combine(Home.Path, "transcripts", "alpha", flowRunId.ToString()), transcripts);
        Assert.Equal(Home.FlowRunTranscriptPath("alpha", flowRunId), transcripts);
        Assert.False(transcripts.StartsWith(work, StringComparison.Ordinal), $"Expected {transcripts} not to start with {work}.");
    }

    [Theory]
    [InlineData("work/alpha")]
    [InlineData("work/alpha/not-a-guid")]
    [InlineData("work/alpha/01a1a451-5d9b-7422-b0b4-7d9fa1901a78/deeper")]
    [InlineData("transcripts/alpha/01a1a451-5d9b-7422-b0b4-7d9fa1901a78")]
    [InlineData("../elsewhere/alpha/01a1a451-5d9b-7422-b0b4-7d9fa1901a78")]
    public void A_path_that_is_not_a_flow_runs_work_directory_has_no_transcript_directory(string relative)
    {
        Action find = () => Home.TranscriptDirectoryOf(Path.Combine(Home.Path, relative));

        DomainException exception = Assert.Throws<DomainException>(find);
        Assert.Contains("is not a flow run's working directory under the node home", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_home_is_prepared_while_another_connection_holds_an_exclusive_lock_on_its_database()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        using var holder = new SqliteConnection($"Data Source={home.DatabasePath};Pooling=False");
        holder.Open();
        Execute(holder, "PRAGMA locking_mode=EXCLUSIVE; CREATE TABLE held (id INTEGER); BEGIN EXCLUSIVE; COMMIT;");

        using (var reader = new SqliteConnection($"Data Source={home.DatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            reader.Open();
            Action read = () => Execute(reader, "SELECT count(*) FROM sqlite_master;", timeoutSeconds: 1);
            SqliteException exception = Assert.Throws<SqliteException>(read);
            Assert.True(exception.SqliteErrorCode == 5, "the holder's exclusive lock keeps every other connection out");
        }

        Action prepare = () => home.EnsureExists().EnsureDatabase();

        prepare();
    }

    private static void Execute(SqliteConnection connection, string sql, int timeoutSeconds = 30)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        command.ExecuteNonQuery();
    }
}
