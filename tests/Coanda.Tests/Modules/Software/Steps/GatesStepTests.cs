using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Sdk.Client;
using Coanda.Sdk.Commands;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Modules.Software.Steps;

public sealed class GatesStepTests : IAsyncLifetime
{
    private const string Project = SoftwareNode.Project;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(45);

    private readonly ScriptedCommandRunner runner = new();
    private SoftwareNode node = null!;

    public async Task InitializeAsync() => node = await SoftwareNode.StartAsync(runner, gatesCommandLimit: Limit);

    public async Task DisposeAsync() => await node.DisposeAsync();

    [Fact]
    public async Task Passing_build_and_test_commands_answer_done_having_run_each_through_the_shell_in_the_work_path()
    {
        Guid flowRunId = Ids.New();

        SystemStepAnswer outcome = await DispatchAsync(Project, flowRunId);

        JsonElement result = Result(outcome, "done");
        Assert.Equal(SoftwareSettings.DefaultBuild, result.GetProperty("build").GetString());
        Assert.Equal(SoftwareSettings.DefaultTest, result.GetProperty("test").GetString());
        Assert.Equal(2, runner.Commands.Count);
        Assert.All(runner.Commands, c => Assert.True(c.WorkingDirectory == node.Home.FlowRunWorkPath(Project, flowRunId)));
        Assert.Equal([SoftwareSettings.DefaultBuild, SoftwareSettings.DefaultTest], runner.ShellLines);
        Assert.Equal(OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/d /s /c \"{SoftwareSettings.DefaultBuild}\"")
            : ("/bin/sh", null), (runner.Commands[0].Program, runner.Commands[0].VerbatimArguments));
    }

    [Fact]
    public async Task Each_command_is_run_with_the_limit_for_one_gates_command()
    {
        Result(await DispatchAsync(Project, Ids.New()), "done");

        Assert.Equal(2, runner.Commands.Count);
        Assert.All(runner.Commands, c => Assert.True(c.Limit == Limit));
    }

    [Fact]
    public async Task A_test_command_ended_at_its_limit_answers_failed_saying_so_and_quoting_what_it_wrote()
    {
        runner.On(c => c.IsShell(SoftwareSettings.DefaultTest), CommandResult.LimitReached("Running 12 tests\n", string.Empty, "Running 12 tests\n"));

        JsonElement result = Result(await DispatchAsync(Project, Ids.New()), "failed");

        string? actual = result.GetProperty("reason").GetString();
        Assert.StartsWith("The test command 'dotnet test' was still running after 45 minutes, the limit for one gates command, and was ended.", actual, StringComparison.Ordinal);
        Assert.Contains("Running 12 tests", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_commands_the_module_s_settings_name_are_the_ones_run()
    {
        await node.Projects.AddAsync(
            Principal.LocalOwner,
            "beta",
            RegisteredProjects.Remote,
            [RegisteredProjects.Setting(SoftwareSettings.Build, "make all"), RegisteredProjects.Setting(SoftwareSettings.Test, "make check")],
            None);

        Result(await DispatchAsync("beta", Ids.New()), "done");
        await node.Services.GetRequiredService<ModuleSettingsService>().SetAsync(
            Principal.LocalOwner, "beta", SoftwareSteps.Module, SoftwareSettings.Test, JsonSerializer.SerializeToElement("make test"), None);
        Result(await DispatchAsync("beta", Ids.New()), "done");

        Assert.Equal(["make all", "make check", "make all", "make test"], runner.ShellLines);
    }

    [Fact]
    public async Task The_gates_run_in_a_project_that_names_no_required_checks_and_no_primary_branch()
    {
        await node.Projects.AddAsync(Principal.LocalOwner, "beta", RegisteredProjects.Remote, [], None);

        Result(await DispatchAsync("beta", Ids.New()), "done");

        Assert.Equal([SoftwareSettings.DefaultBuild, SoftwareSettings.DefaultTest], runner.ShellLines);
    }

    [Fact]
    public async Task A_failing_build_answers_failed_naming_the_command_and_its_exit_code_and_runs_no_test()
    {
        runner.On(c => c.IsShell(SoftwareSettings.DefaultBuild), new CommandResult(1, "Build FAILED.\n", "error CS1002\n", "error CS1002\nBuild FAILED.\n"));

        JsonElement result = Result(await DispatchAsync(Project, Ids.New()), "failed");

        string? actual = result.GetProperty("reason").GetString();
        Assert.StartsWith("The build command 'dotnet build' exited with code 1.", actual, StringComparison.Ordinal);
        Assert.Contains("error CS1002\nBuild FAILED.", actual, StringComparison.Ordinal);
        Assert.Equal([SoftwareSettings.DefaultBuild], runner.ShellLines);
    }

    [Fact]
    public async Task A_failing_test_after_a_passing_build_answers_failed_naming_the_test_command()
    {
        runner.On(c => c.IsShell(SoftwareSettings.DefaultTest), new CommandResult(127, string.Empty, "sh: dotnet: not found\n", "sh: dotnet: not found\n"));

        JsonElement result = Result(await DispatchAsync(Project, Ids.New()), "failed");

        string? actual = result.GetProperty("reason").GetString();
        Assert.StartsWith("The test command 'dotnet test' exited with code 127.", actual, StringComparison.Ordinal);
        Assert.Contains("not found", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_commands_output_is_cut_to_its_last_hundred_lines()
    {
        string output = string.Join('\n', Enumerable.Range(1, 150).Select(n => $"line {n}")) + "\n";
        runner.On(c => c.IsShell(SoftwareSettings.DefaultBuild), new CommandResult(2, output, string.Empty, output));

        string reason = Result(await DispatchAsync(Project, Ids.New()), "failed").GetProperty("reason").GetString() ?? string.Empty;

        Assert.Contains("The last 100 lines of its output:\nline 51\n", reason, StringComparison.Ordinal);
        Assert.EndsWith("line 150", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("line 50\n", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_url_with_user_information_in_failing_output_is_stored_without_it()
    {
        const string Output = "fatal: unable to access 'https://bot:s3cret@example.invalid/repo.git/'\nfrom git@example.invalid:team/repo.git\n";
        runner.On(c => c.IsShell(SoftwareSettings.DefaultBuild), new CommandResult(1, Output, string.Empty, Output));

        string reason = Result(await DispatchAsync(Project, Ids.New()), "failed").GetProperty("reason").GetString() ?? string.Empty;

        Assert.Contains("https://example.invalid/repo.git/", reason, StringComparison.Ordinal);
        Assert.Contains("git@example.invalid:team/repo.git", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_that_cannot_be_started_pauses_with_the_perform_again_lever()
    {
        runner.On(c => c.IsShell(SoftwareSettings.DefaultBuild), (command, _) =>
            Task.FromException<CommandResult>(new CommandNotStartedException(command, "No such file or directory")));

        SystemStepAnswer.Paused paused = AsPaused(await DispatchAsync(Project, await HeldAsync()));

        Assert.StartsWith("Step 'gates' could not start the shell to run the build command 'dotnet build'", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("No such file or directory", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("gates"), paused.Lever);
    }

    [Fact]
    public async Task A_project_the_core_shows_the_module_no_record_of_is_refused_quoting_the_core_and_runs_nothing()
    {
        CoreApiException refused = await Assert.ThrowsAsync<CoreApiException>(() => DispatchAsync("nowhere", Ids.New()));

        Assert.Contains("project 'nowhere'", refused.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    private Task<SystemStepAnswer> DispatchAsync(string project, Guid flowRunId) =>
        node.DispatchAsync(SoftwareSteps.Gates, Ids.New(), flowRunId, project: project);

    /// <summary>A flow run sitting in the gates step, so the step's reasons and levers name it.</summary>
    private async Task<Guid> HeldAsync() =>
        (await node.HeldAsync((await node.TaskAsync("Gate me")).Id, "gates")).FlowRunId;

    private static JsonElement Result(SystemStepAnswer outcome, string verdict)
    {
        JsonElement result = Assert.IsType<SystemStepAnswer.Done>(outcome).Result;
        Assert.Equal(verdict, result.GetProperty("verdict").GetString());
        Assert.Equal(SoftwareSteps.Gates, result.GetProperty("systemStep").GetString());
        return result;
    }

    private static SystemStepAnswer.Paused AsPaused(SystemStepAnswer outcome) => Assert.IsType<SystemStepAnswer.Paused>(outcome);
}
