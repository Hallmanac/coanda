using System.Diagnostics;
using System.Globalization;
using Coanda.Daemon.Commands;
using Coanda.Tests.Daemon.Executors;

namespace Coanda.Tests.Daemon.Commands;

/// <summary>
/// The command runner that starts real processes, run only against the fake executable built from
/// the solution (<c>tests/Coanda.FakeClaude</c> in its <c>--fake-command</c> mode), directly or
/// through the platform's shell.
/// </summary>
public sealed class ProcessCommandRunnerTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly ProcessCommandRunner runner = new();
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "coanda-runner-tests", Guid.NewGuid().ToString("N"));
    private readonly List<int> started = [];

    public ProcessCommandRunnerTests() => Directory.CreateDirectory(scratch);

    public void Dispose()
    {
        foreach (int pid in started)
        {
            try
            {
                using Process left = Process.GetProcessById(pid);
                left.Kill(entireProcessTree: true);
            }
            catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
            {
            }
        }

        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_fakes_exit_code_and_its_output_are_returned()
    {
        CommandResult result = await runner.RunAsync(Fake("exit", "3", "to stdout\n", "to stderr\n"), CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("to stdout\n", result.StandardOutput);
        Assert.Equal("to stderr\n", result.StandardError);
        Assert.Contains("to stdout\n", result.Combined, StringComparison.Ordinal);
        Assert.Contains("to stderr\n", result.Combined, StringComparison.Ordinal);
        Assert.Equal("to stdout\nto stderr\n".Length, result.Combined.Length);
    }

    [Fact]
    public async Task An_executable_that_does_not_exist_is_reported_as_unable_to_start()
    {
        var missing = new CommandLine(Path.Combine(scratch, "no-such-program"), [], scratch, CommandLine.NoVariables);

        CommandNotStartedException exception = await Assert.ThrowsAsync<CommandNotStartedException>(() => runner.RunAsync(missing, CancellationToken.None));
        Assert.Contains("no-such-program could not be started", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_fake_finds_its_standard_input_already_at_its_end()
    {
        CommandResult result = await runner.RunAsync(Fake("stdin"), CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stdin:0", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task The_runner_returns_when_the_fake_exits_while_a_child_it_started_still_holds_its_output()
    {
        string pidFile = Path.Combine(scratch, "child.pid");

        CommandResult result = await runner.RunAsync(Fake("leave-child", pidFile), CancellationToken.None).WaitAsync(Patience);
        int child = Pid(pidFile);
        started.Add(child);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("parent exits", result.StandardOutput, StringComparison.Ordinal);
        Assert.True(Running(child), "the child sleeps on with the output still open, and the runner did not wait for it");
    }

    [Fact]
    public async Task The_runner_lets_go_of_the_output_a_child_still_writes_to_once_it_returns()
    {
        if (OperatingSystem.IsWindows())
        {
            // The fake's writing child is Unix-only; see its leave-writer mode.
            return;
        }

        string pidFile = Path.Combine(scratch, "writer.pid");

        CommandResult result = await runner.RunAsync(Fake("leave-writer", pidFile), CancellationToken.None).WaitAsync(Patience);
        int child = Pid(pidFile);
        started.Add(child);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("parent exits", result.StandardOutput, StringComparison.Ordinal);
        await UntilAsync(() => !Running(child));
    }

    [Fact]
    public async Task Cancelling_ends_the_fake_and_the_child_it_started()
    {
        string pidFile = Path.Combine(scratch, "child.pid");
        using var cancel = new CancellationTokenSource();

        Task<CommandResult> running = runner.RunAsync(Fake("hold", pidFile), cancel.Token);
        await UntilAsync(() => File.Exists(pidFile) && File.Exists(pidFile + ".parent") && TryPid(pidFile) is not null);
        int fake = Pid(pidFile + ".parent");
        int child = Pid(pidFile);
        started.AddRange([fake, child]);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Patience));
        await UntilAsync(() => !Running(fake) && !Running(child));
    }

    [Fact]
    public async Task A_command_past_its_limit_is_ended_with_its_child_and_answers_timed_out_with_what_it_wrote()
    {
        string pidFile = Path.Combine(scratch, "child.pid");

        CommandResult result = await runner.RunAsync(Fake("hold", pidFile) with { Limit = TimeSpan.FromSeconds(2) }, CancellationToken.None).WaitAsync(Patience);
        int fake = Pid(pidFile + ".parent");
        int child = Pid(pidFile);
        started.AddRange([fake, child]);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal(CommandResult.TimedOutExitCode, result.ExitCode);
        Assert.Contains("holding", result.StandardOutput, StringComparison.Ordinal);
        await UntilAsync(() => !Running(fake) && !Running(child));
    }

    [Fact]
    public async Task A_command_that_exits_within_its_limit_is_not_timed_out()
    {
        CommandResult result = await runner.RunAsync(Fake("exit", "0", "done\n", "") with { Limit = TimeSpan.FromMinutes(1) }, CancellationToken.None).WaitAsync(Patience);

        Assert.False(result.TimedOut);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Only_the_end_of_output_longer_than_the_runner_keeps_is_returned_and_marked_truncated()
    {
        const int Lines = 300_000;

        CommandResult result = await runner.RunAsync(Fake("flood", Lines.ToString(CultureInfo.InvariantCulture)), CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.Equal(ProcessCommandRunner.KeptCharacters, result.StandardOutput.Length);
        Assert.Equal(ProcessCommandRunner.KeptCharacters, result.Combined.Length);
        Assert.EndsWith($"line {Lines}\n", result.StandardOutput.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_within_what_the_runner_keeps_is_not_marked_truncated()
    {
        CommandResult result = await runner.RunAsync(Fake("flood", "10"), CancellationToken.None).WaitAsync(Patience);

        Assert.False(result.Truncated);
        string actual = result.StandardOutput.ReplaceLineEndings("\n");
        Assert.StartsWith("line 1\n", actual, StringComparison.Ordinal);
        Assert.EndsWith("line 10\n", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_platforms_shell_delivers_an_argument_holding_a_space_and_a_double_quote_intact()
    {
        const string Awkward = "a \"b c";
        string line = OperatingSystem.IsWindows()
            ? $"\"{FakeClaude.Executable}\" {Coanda.Testing.FakeClaude.Program.CommandFlag} args \"a \\\"b c\""
            : $"'{FakeClaude.Executable}' {Coanda.Testing.FakeClaude.Program.CommandFlag} args '{Awkward}'";

        CommandResult result = await runner.RunAsync(CommandLine.Shell(line, scratch), CancellationToken.None).WaitAsync(Patience);

        Assert.True(result.ExitCode == 0, result.Combined);
        Assert.Equal($"[{Awkward}]\n", result.StandardOutput.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_shells_exit_for_a_command_it_cannot_find_is_returned_as_the_commands_exit()
    {
        CommandResult result = await runner.RunAsync(CommandLine.Shell("coanda-no-such-command-anywhere", scratch), CancellationToken.None).WaitAsync(Patience);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("coanda-no-such-command-anywhere", result.Combined, StringComparison.Ordinal);
    }

    private CommandLine Fake(params string[] arguments) =>
        new(FakeClaude.Executable, [Coanda.Testing.FakeClaude.Program.CommandFlag, .. arguments], scratch, CommandLine.NoVariables);

    private static int Pid(string file) => TryPid(file) ?? throw new InvalidOperationException($"{file} holds no pid.");

    private static int? TryPid(string file)
    {
        try
        {
            return int.TryParse(File.ReadAllText(file), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) ? pid : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool Running(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var patience = new CancellationTokenSource(Patience);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), patience.Token);
        }
    }
}
