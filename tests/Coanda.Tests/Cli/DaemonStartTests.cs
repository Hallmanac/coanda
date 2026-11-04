using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Executors;
using Coanda.Tests.Daemon;
using Xunit.Sdk;

namespace Coanda.Tests.Cli;

/// <summary>
/// What <c>coa daemon start</c> does when the built <c>coa</c> executable is run against a temporary
/// node home: the daemon it starts is detached from it, and a daemon that exits before it is ready
/// is reported at once with what it wrote. Every wait is bounded, so a regression fails rather than hangs.
/// </summary>
public sealed partial class DaemonStartTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(10);

    /// <summary>The CLI's executable, built beside the test assembly because the tests reference its project.</summary>
    private static readonly string Coa = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "coa.exe" : "coa");

    [Fact]
    public async Task Daemon_start_with_its_output_piped_returns_and_leaves_the_daemon_running()
    {
        using var temp = new TemporaryHome();
        try
        {
            Ran started = await RunAsync(temp.Home, "daemon", "start");

            Assert.True(started.ExitCode == 0, started.Text);
            DaemonInfo info = DaemonInfo.Load(temp.Home) ?? throw new XunitException($"The start wrote no address file: {started.Text}");
            Assert.Contains(Squashed($"Started the daemon on {info.BaseAddress} (pid {info.Pid}); log at {temp.Home.LogPath}."), Squashed(started.Output), StringComparison.Ordinal);

            Ran status = await RunAsync(temp.Home, "daemon", "status");
            Assert.DoesNotContain("No daemon is running", status.Output, StringComparison.Ordinal);
            Assert.Contains(info.Pid.ToString(CultureInfo.InvariantCulture), status.Output, StringComparison.Ordinal);
        }
        finally
        {
            await StopAsync(temp.Home);
        }
    }

    [Fact]
    public async Task On_macOS_and_Linux_the_daemon_leads_a_process_group_apart_from_its_callers()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TemporaryHome();
        try
        {
            Ran started = await RunAsync(temp.Home, "daemon", "start");
            Assert.True(started.ExitCode == 0, started.Text);
            DaemonInfo info = DaemonInfo.Load(temp.Home) ?? throw new XunitException($"The start wrote no address file: {started.Text}");

            // coa is started in this process's group, so its group is the caller's.
            int callers = await ProcessGroupAsync(Environment.ProcessId);
            int daemons = await ProcessGroupAsync(info.Pid);

            // the daemon leads a process group of its own
            Assert.Equal(info.Pid, daemons);
            Assert.NotEqual(callers, daemons);
        }
        finally
        {
            await StopAsync(temp.Home);
        }
    }

    [Fact]
    public async Task A_daemon_that_fails_after_opening_its_log_is_reported_at_once_with_what_it_logged()
    {
        using var temp = new TemporaryHome();

        // The CLI's own checks skip a database path that is not a file; the daemon fails creating it.
        Directory.CreateDirectory(temp.Home.DatabasePath);

        Ran started = await RunAsync(temp.Home, "daemon", "start");

        Assert.True(started.ExitCode != 0, started.Text);
        Assert.True(started.Elapsed < FailureBound, $"Expected the start to fail within {FailureBound}, but it took {started.Elapsed}.");
        string text = Squashed(started.Text);
        Assert.Matches(@"^Thedaemonexited(withcode-?\d+)?beforeitwasready\.", text);
        Assert.Contains(Squashed($"It wrote this to its log at {temp.Home.LogPath}:"), text, StringComparison.Ordinal);
        Assert.Contains("EnsureDatabase", text, StringComparison.Ordinal);
        Assert.Contains(Squashed(temp.Home.DatabasePath), text, StringComparison.Ordinal);
        Assert.Null(DaemonInfo.Load(temp.Home));
    }

    [Fact]
    public async Task A_daemon_that_fails_before_opening_its_log_is_reported_at_once_with_what_it_printed()
    {
        using var temp = new TemporaryHome();

        // The CLI's own checks skip a log path that is not a file; the daemon fails opening it.
        Directory.CreateDirectory(temp.Home.LogPath);

        Ran started = await RunAsync(temp.Home, "daemon", "start");

        Assert.True(started.ExitCode != 0, started.Text);
        Assert.True(started.Elapsed < FailureBound, $"Expected the start to fail within {FailureBound}, but it took {started.Elapsed}.");
        string text = Squashed(started.Text);
        Assert.Matches(@"^Thedaemonexited(withcode-?\d+)?beforeitwasready\.", text);
        Assert.Contains(Squashed($"It printed this outside its log, to {temp.Home.DaemonOutputPath}:"), text, StringComparison.Ordinal);
        Assert.Contains("OpenLog", text, StringComparison.Ordinal);
        Assert.Contains(Squashed(temp.Home.LogPath), text, StringComparison.Ordinal);
        Assert.True(File.Exists(temp.Home.DaemonOutputPath), "what the daemon printed outside its log is kept after the start");
    }

    /// <summary>
    /// Runs <c>coa</c> with its standard output and error redirected to pipes, until it exits and
    /// both pipes reach their end, each within <see cref="Bound"/>.
    /// </summary>
    private static async Task<Ran> RunAsync(NodeHome home, params string[] arguments)
    {
        var start = new ProcessStartInfo(Coa)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[NodeHome.EnvironmentVariable] = home.Path;
        start.Environment.Remove(SessionAccess.TokenVariable);
        start.Environment.Remove(SessionAccess.HomeVariable);

        var watch = Stopwatch.StartNew();
        using Process process = Process.Start(start) ?? throw new XunitException($"{Coa} did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        string command = $"coa {string.Join(' ', arguments)}";
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw new XunitException($"{command} did not return within {Bound.TotalSeconds:0} seconds.");
        }

        TimeSpan elapsed = watch.Elapsed;
        try
        {
            await Task.WhenAll(output, error).WaitAsync(Bound);
        }
        catch (TimeoutException)
        {
            throw new XunitException($"{command} returned, but its output pipes did not reach their end within {Bound.TotalSeconds:0} seconds.");
        }

        return new Ran(process.ExitCode, await output, await error, elapsed);
    }

    /// <summary>Stops the daemon a test started, if one is recorded, and waits for its process to end, killing it if it does not.</summary>
    private static async Task StopAsync(NodeHome home)
    {
        if (DaemonInfo.Load(home) is not { } info)
        {
            return;
        }

        Process daemon;
        try
        {
            daemon = Process.GetProcessById(info.Pid);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (daemon)
        {
            try
            {
                _ = await RunAsync(home, "daemon", "stop");
                await daemon.WaitForExitAsync(CancellationToken.None).WaitAsync(Bound);
            }
            catch (Exception notStopped) when (notStopped is TimeoutException or XunitException)
            {
                daemon.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task<int> ProcessGroupAsync(int pid)
    {
        var start = new ProcessStartInfo("ps") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-o", "pgid=", "-p", pid.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        using Process ps = Process.Start(start) ?? throw new XunitException("ps did not start.");
        string output = await ps.StandardOutput.ReadToEndAsync(CancellationToken.None).WaitAsync(Bound);
        await ps.WaitForExitAsync(CancellationToken.None).WaitAsync(Bound);
        return int.Parse(output.Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The text without its whitespace or its terminal escape sequences, since the CLI wraps what it
    /// prints at the width of a console it cannot see, and colors it when it detects a CI runner.
    /// </summary>
    private static string Squashed(string text) => Whitespace().Replace(Escapes().Replace(text, string.Empty), string.Empty);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>ANSI control sequences (colors and styles) and operating system commands (links).</summary>
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)")]
    private static partial Regex Escapes();

    private sealed record Ran(int ExitCode, string Output, string Error, TimeSpan Elapsed)
    {
        public string Text => Output + Error;
    }
}
