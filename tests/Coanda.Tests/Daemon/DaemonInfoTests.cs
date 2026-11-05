using System.Text;
using Coanda.Daemon.Api;

namespace Coanda.Tests.Daemon;

/// <summary>
/// How the CLI reads the running daemon's address file while the daemon may still be writing or
/// deleting it: a file that is not there or not yet whole reads as no daemon, never as an error.
/// </summary>
public sealed class DaemonInfoTests
{
    [Fact]
    public void A_home_without_an_address_file_has_no_daemon()
    {
        using var temp = new TemporaryHome();

        Assert.Null(DaemonInfo.Load(temp.Home));
    }

    [Fact]
    public void An_address_file_the_daemon_holds_open_half_written_reads_as_no_daemon_yet()
    {
        using var temp = new TemporaryHome();

        // Held the way Save holds it, so on Windows a read sharing only read would be refused.
        using var writing = new FileStream(temp.Home.DaemonInfoPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        writing.Write(Encoding.UTF8.GetBytes("{ \"Pid\": 1, \"Port\""));
        writing.Flush();

        Assert.Null(DaemonInfo.Load(temp.Home));
    }

    [Fact]
    public void A_saved_address_file_reads_back_as_written()
    {
        using var temp = new TemporaryHome();
        var saved = new DaemonInfo(42, 5123, "token", DateTimeOffset.UnixEpoch, "1.0.0");
        saved.Save(temp.Home);

        Assert.Equal(saved, DaemonInfo.Load(temp.Home));
    }
}
