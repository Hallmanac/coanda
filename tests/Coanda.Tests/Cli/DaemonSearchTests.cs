using System.Text;
using Coanda.Cli;
using Coanda.Tests.Daemon;

namespace Coanda.Tests.Cli;

/// <summary>
/// How the CLI's search for a running daemon reads an address file the daemon may still be
/// writing: a file held open by the daemon's save, one half written, and one absent all mean no
/// daemon is running yet, and none of them throws.
/// </summary>
public sealed class DaemonSearchTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task An_address_file_the_daemon_holds_open_finds_no_daemon_yet()
    {
        using var temp = new TemporaryHome();
        using var client = new DaemonClient(temp.Home, sessionToken: null);

        // Held the way Save holds it, just after Save's create and before its first byte.
        using var writing = new FileStream(temp.Home.DaemonInfoPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        Assert.Null(await client.FindRunningAsync(None));
    }

    [Fact]
    public async Task A_half_written_address_file_finds_no_daemon_yet()
    {
        using var temp = new TemporaryHome();
        using var client = new DaemonClient(temp.Home, sessionToken: null);
        await File.WriteAllTextAsync(temp.Home.DaemonInfoPath, "{ \"Pid\": 1, \"Port\"", Encoding.UTF8, None);

        Assert.Null(await client.FindRunningAsync(None));
    }

    [Fact]
    public async Task A_home_without_an_address_file_finds_no_daemon()
    {
        using var temp = new TemporaryHome();
        using var client = new DaemonClient(temp.Home, sessionToken: null);

        Assert.Null(await client.FindRunningAsync(None));
    }
}
