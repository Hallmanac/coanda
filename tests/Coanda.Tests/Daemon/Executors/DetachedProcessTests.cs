using System.Diagnostics;
using System.Globalization;
using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Tests.Daemon.Executors;

public sealed class DetachedProcessTests
{
    [Fact]
    public void A_running_process_on_macOS_has_the_identity_its_start_time_gave_it_before_the_upgrade()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var sleeper = Process.Start("/bin/sleep", "30");
        try
        {
            Assert.Equal((Previous(sleeper.Id), false), DetachedProcess.Posix.IdentityOf(sleeper.Id));
        }
        finally
        {
            sleeper.Kill();
            sleeper.WaitForExit();
        }

        // Every other process this user can read too, so start times with every kind of fraction are compared.
        int compared = 0;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                string? before = TryPrevious(process.Id);
                (string Identity, bool Exited)? now = DetachedProcess.Posix.IdentityOf(process.Id);
                if (before is not null && now is { Exited: false } && TryPrevious(process.Id) == before)
                {
                    Assert.Equal(before, now.Value.Identity);
                    compared++;
                }
            }
        }

        Assert.True(compared > 10, $"Only {compared} processes could be compared.");
    }

    [Fact]
    public void A_process_that_exited_unreaped_has_the_identity_it_was_started_with()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var input = File.OpenHandle("/dev/null");
        using var output = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.Write);
        using DetachedProcess process = DetachedProcess.Start("/bin/sleep", ["30"], new Dictionary<string, string>(), "/", input, output, output);
        Assert.NotEqual(string.Empty, process.Identity);
        Assert.Equal((process.Identity, false), DetachedProcess.Posix.IdentityOf(process.Id));

        // Killed by its group alone, so nothing waits on it and it stays in the process table.
        DetachedProcess.Posix.KillGroup(process.Id);
        Hold(() => FakeClaude.Finished(process.Id));

        Assert.Equal((process.Identity, true), DetachedProcess.Posix.IdentityOf(process.Id));
        Assert.True(process.HasExited);
        Assert.Null(DetachedProcess.Posix.IdentityOf(process.Id));
    }

    /// <summary>The identity the daemon read on macOS before the upgrade.</summary>
    private static string Previous(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return $"start:{process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? TryPrevious(int pid)
    {
        try
        {
            return Previous(pid);
        }
        catch (Exception unreadable) when (unreadable is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static void Hold(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The condition did not hold within 30 seconds.");
            Thread.Sleep(20);
        }
    }
}
