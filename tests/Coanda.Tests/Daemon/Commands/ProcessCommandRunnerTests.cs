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

        result.ExitCode.Should().Be(3);
        result.StandardOutput.Should().Be("to stdout\n");
        result.StandardError.Should().Be("to stderr\n");
        result.Combined.Should().Contain("to stdout\n").And.Contain("to stderr\n").And.HaveLength("to stdout\nto stderr\n".Length);
    }

    [Fact]
    public async Task An_executable_that_does_not_exist_is_reported_as_unable_to_start()
    {
        var missing = new CommandLine(Path.Combine(scratch, "no-such-program"), [], scratch, CommandLine.NoVariables);

        await FluentActions.Awaiting(() => runner.RunAsync(missing, CancellationToken.None))
            .Should().ThrowAsync<CommandNotStartedException>().WithMessage("*no-such-program could not be started*");
    }

    [Fact]
    public async Task The_fake_finds_its_standard_input_already_at_its_end()
    {
        CommandResult result = await runner.RunAsync(Fake("stdin"), CancellationToken.None).WaitAsync(Patience);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Trim().Should().Be("stdin:0");
    }

    [Fact]
    public async Task The_runner_returns_when_the_fake_exits_while_a_child_it_started_still_holds_its_output()
    {
        string pidFile = Path.Combine(scratch, "child.pid");

        CommandResult result = await runner.RunAsync(Fake("leave-child", pidFile), CancellationToken.None).WaitAsync(Patience);
        int child = Pid(pidFile);
        started.Add(child);

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("parent exits");
        Running(child).Should().BeTrue("the child sleeps on with the output still open, and the runner did not wait for it");
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

        await FluentActions.Awaiting(() => running.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        await UntilAsync(() => !Running(fake) && !Running(child));
    }

    [Fact]
    public async Task The_platforms_shell_delivers_an_argument_holding_a_space_and_a_double_quote_intact()
    {
        const string Awkward = "a \"b c";
        string line = OperatingSystem.IsWindows()
            ? $"\"{FakeClaude.Executable}\" {Coanda.Testing.FakeClaude.Program.CommandFlag} args \"a \\\"b c\""
            : $"'{FakeClaude.Executable}' {Coanda.Testing.FakeClaude.Program.CommandFlag} args '{Awkward}'";

        CommandResult result = await runner.RunAsync(CommandLine.Shell(line, scratch), CancellationToken.None).WaitAsync(Patience);

        result.ExitCode.Should().Be(0, result.Combined);
        result.StandardOutput.ReplaceLineEndings("\n").Should().Be($"[{Awkward}]\n");
    }

    [Fact]
    public async Task The_shells_exit_for_a_command_it_cannot_find_is_returned_as_the_commands_exit()
    {
        CommandResult result = await runner.RunAsync(CommandLine.Shell("coanda-no-such-command-anywhere", scratch), CancellationToken.None).WaitAsync(Patience);

        result.ExitCode.Should().NotBe(0);
        result.Combined.Should().Contain("coanda-no-such-command-anywhere");
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
