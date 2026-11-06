using Coanda.Core;
using Coanda.Tests.Daemon.Commands;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// A software node that fails to start leaves no daemon running and no home behind it, and a node disposed
/// a second time, as a test class's own disposal does after a test replaced its node and the replacement
/// failed to start, disposes nothing again.
/// </summary>
public sealed class SoftwareNodeTests
{
    private static readonly string TestHomes = Path.Combine(Path.GetTempPath(), "coanda-tests");

    [Fact]
    public async Task A_start_that_fails_once_its_daemon_has_started_stops_that_daemon_and_removes_its_home()
    {
        var runner = new ScriptedCommandRunner();

        // the second registration of the same project is refused, after the daemon has started
        await Assert.ThrowsAsync<DomainException>(() => SoftwareNode.StartAsync(runner, projects: [SoftwareNode.Project, SoftwareNode.Project]));

        string home = HomeOf(runner);
        Assert.False(Directory.Exists(home), $"The home {home} of the node that failed to start is still there.");
    }

    [Fact]
    public async Task A_node_disposed_a_second_time_disposes_nothing_again()
    {
        SoftwareNode node = await SoftwareNode.StartAsync();
        await node.DisposeAsync();

        Exception? again = await Record.ExceptionAsync(() => node.DisposeAsync().AsTask());

        Assert.Null(again);
    }

    /// <summary>The home the node's daemon ran its commands under, read from where it ran them.</summary>
    private static string HomeOf(ScriptedCommandRunner runner)
    {
        string under = Assert.Single(
            runner.Commands.Select(c => c.WorkingDirectory).Where(d => d.StartsWith(TestHomes, StringComparison.Ordinal)).Select(d => d[(TestHomes.Length + 1)..].Split(Path.DirectorySeparatorChar)[0]).Distinct());
        return Path.Combine(TestHomes, under);
    }
}
