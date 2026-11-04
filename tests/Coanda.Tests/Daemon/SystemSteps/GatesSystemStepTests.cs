using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using ProjectDefaults = Coanda.Core.Projects.Project;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Commands;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Projects;
using Coanda.Tests.Daemon.Commands;
using Fisher;

namespace Coanda.Tests.Daemon.SystemSteps;

public sealed class GatesSystemStepTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal FlowRunPrincipal = new("flow-run:under-test", Role.Agent);
    private static readonly FlowStep Step = new() { Name = "gates", SystemStep = SoftwareSystemSteps.Gates };
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(45);

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private IDocumentStore store = null!;
    private ProjectService projects = null!;
    private GatesSystemStep systemStep = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        projects = new ProjectService(store, TimeProvider.System);
        systemStep = new GatesSystemStep(projects, runner, temp.Home, Limit);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task Passing_build_and_test_commands_answer_done_having_run_each_through_the_shell_in_the_work_path()
    {
        Guid flowRunId = Ids.New();

        SystemStepOutcome outcome = await PerformAsync(Project, flowRunId);

        JsonElement result = Result(outcome, "done");
        Assert.Equal(ProjectDefaults.DefaultBuild, result.GetProperty("build").GetString());
        Assert.Equal(ProjectDefaults.DefaultTest, result.GetProperty("test").GetString());
        Assert.Equal(2, runner.Commands.Count);
        Assert.All(runner.Commands, c => Assert.True(c.WorkingDirectory == temp.Home.FlowRunWorkPath(Project, flowRunId)));
        Assert.Equal([ProjectDefaults.DefaultBuild, ProjectDefaults.DefaultTest], runner.ShellLines);
        Assert.Equal(OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/d /s /c \"{ProjectDefaults.DefaultBuild}\"")
            : ("/bin/sh", null), (runner.Commands[0].Program, runner.Commands[0].VerbatimArguments));
    }

    [Fact]
    public async Task Each_command_is_run_with_the_limit_for_one_gates_command()
    {
        Result(await PerformAsync(Project, Ids.New()), "done");

        Assert.Equal(2, runner.Commands.Count);
        Assert.All(runner.Commands, c => Assert.True(c.Limit == Limit));
    }

    [Fact]
    public async Task A_test_command_ended_at_its_limit_answers_failed_saying_so_and_quoting_what_it_wrote()
    {
        runner.On(c => c.IsShell(ProjectDefaults.DefaultTest), CommandResult.LimitReached("Running 12 tests\n", string.Empty, "Running 12 tests\n"));

        JsonElement result = Result(await PerformAsync(Project, Ids.New()), "failed");

        string? actual = result.GetProperty("reason").GetString();
        Assert.StartsWith("The test command 'dotnet test' was still running after 45 minutes, the limit for one gates command, and was ended.", actual, StringComparison.Ordinal);
        Assert.Contains("Running 12 tests", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_commands_a_project_names_are_the_ones_run()
    {
        await projects.AddAsync(Principal.LocalOwner, "beta", RegisteredProjects.Remote, "main", "make all", "make check", None);

        Result(await PerformAsync("beta", Ids.New()), "done");

        Assert.Equal(["make all", "make check"], runner.ShellLines);
    }

    [Fact]
    public async Task A_failing_build_answers_failed_naming_the_command_and_its_exit_code_and_runs_no_test()
    {
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), new CommandResult(1, "Build FAILED.\n", "error CS1002\n", "error CS1002\nBuild FAILED.\n"));

        JsonElement result = Result(await PerformAsync(Project, Ids.New()), "failed");

        string? actual = result.GetProperty("reason").GetString();
        Assert.StartsWith("The build command 'dotnet build' exited with code 1.", actual, StringComparison.Ordinal);
        Assert.Contains("error CS1002\nBuild FAILED.", actual, StringComparison.Ordinal);
        Assert.Equal([ProjectDefaults.DefaultBuild], runner.ShellLines);
    }

    [Fact]
    public async Task A_failing_test_after_a_passing_build_answers_failed_naming_the_test_command()
    {
        runner.On(c => c.IsShell(ProjectDefaults.DefaultTest), new CommandResult(127, string.Empty, "sh: dotnet: not found\n", "sh: dotnet: not found\n"));

        JsonElement result = Result(await PerformAsync(Project, Ids.New()), "failed");

        string? actual = result.GetProperty("reason").GetString();
        Assert.StartsWith("The test command 'dotnet test' exited with code 127.", actual, StringComparison.Ordinal);
        Assert.Contains("not found", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_commands_output_is_cut_to_its_last_hundred_lines()
    {
        string output = string.Join('\n', Enumerable.Range(1, 150).Select(n => $"line {n}")) + "\n";
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), new CommandResult(2, output, string.Empty, output));

        string reason = Result(await PerformAsync(Project, Ids.New()), "failed").GetProperty("reason").GetString() ?? string.Empty;

        Assert.Contains("The last 100 lines of its output:\nline 51\n", reason, StringComparison.Ordinal);
        Assert.EndsWith("line 150", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("line 50\n", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_url_with_user_information_in_failing_output_is_stored_without_it()
    {
        const string Output = "fatal: unable to access 'https://bot:s3cret@example.invalid/repo.git/'\nfrom git@example.invalid:team/repo.git\n";
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), new CommandResult(1, Output, string.Empty, Output));

        string reason = Result(await PerformAsync(Project, Ids.New()), "failed").GetProperty("reason").GetString() ?? string.Empty;

        Assert.Contains("https://example.invalid/repo.git/", reason, StringComparison.Ordinal);
        Assert.Contains("git@example.invalid:team/repo.git", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_that_cannot_be_started_pauses_with_the_perform_again_lever()
    {
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), (command, _) =>
            Task.FromException<CommandResult>(new CommandNotStartedException(command, "No such file or directory")));

        SystemStepOutcome.Paused paused = AsPaused(await PerformAsync(Project, Ids.New()));

        Assert.StartsWith("Step 'gates' could not start the shell to run the build command 'dotnet build'", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("No such file or directory", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("gates"), paused.Lever);
    }

    [Fact]
    public async Task A_project_with_no_record_pauses_naming_the_step_and_the_project_and_runs_nothing()
    {
        SystemStepOutcome.Paused paused = AsPaused(await PerformAsync("nowhere", Ids.New()));

        Assert.StartsWith("Step 'gates'", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("project 'nowhere'", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("Project 'nowhere' is not registered", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("gates"), paused.Lever);
        Assert.Empty(runner.Commands);
    }

    private ValueTask<SystemStepOutcome> PerformAsync(string project, Guid flowRunId) =>
        systemStep.PerformAsync(new SystemStepRequest(FlowRunPrincipal, project, flowRunId, Ids.New(), ShippedFlows.Build, Step, Resumed: false), None);

    private static JsonElement Result(SystemStepOutcome outcome, string verdict)
    {
        JsonElement result = Assert.IsType<SystemStepOutcome.Done>(outcome).Result;
        Assert.Equal(verdict, result.GetProperty("verdict").GetString());
        Assert.Equal(SoftwareSystemSteps.Gates, result.GetProperty("systemStep").GetString());
        return result;
    }

    private static SystemStepOutcome.Paused AsPaused(SystemStepOutcome outcome) => Assert.IsType<SystemStepOutcome.Paused>(outcome);
}
