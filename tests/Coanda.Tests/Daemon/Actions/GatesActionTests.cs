using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Contours;
using ProjectDefaults = Coanda.Core.Projects.Project;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Commands;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Projects;
using Coanda.Tests.Daemon.Commands;
using Fisher;

namespace Coanda.Tests.Daemon.Actions;

public sealed class GatesActionTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal FlowPrincipal = new("flow:under-test", Role.Agent);
    private static readonly ContourStep Step = new() { Name = "gates", Action = SoftwareActions.Gates };
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(45);

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private IDocumentStore store = null!;
    private ProjectService projects = null!;
    private GatesAction action = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        projects = new ProjectService(store, TimeProvider.System);
        action = new GatesAction(projects, runner, temp.Home, Limit);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task Passing_build_and_test_commands_answer_done_having_run_each_through_the_shell_in_the_work_path()
    {
        Guid flowId = Ids.New();

        ActionOutcome outcome = await PerformAsync(Project, flowId);

        JsonElement result = Result(outcome, "done");
        result.GetProperty("build").GetString().Should().Be(ProjectDefaults.DefaultBuild);
        result.GetProperty("test").GetString().Should().Be(ProjectDefaults.DefaultTest);
        runner.Commands.Should().HaveCount(2).And.OnlyContain(c => c.WorkingDirectory == temp.Home.FlowWorkPath(Project, flowId));
        runner.ShellLines.Should().Equal(ProjectDefaults.DefaultBuild, ProjectDefaults.DefaultTest);
        (runner.Commands[0].Program, runner.Commands[0].VerbatimArguments).Should().Be(OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/d /s /c \"{ProjectDefaults.DefaultBuild}\"")
            : ("/bin/sh", null));
    }

    [Fact]
    public async Task Each_command_is_run_with_the_limit_for_one_gates_command()
    {
        Result(await PerformAsync(Project, Ids.New()), "done");

        runner.Commands.Should().HaveCount(2).And.OnlyContain(c => c.Limit == Limit);
    }

    [Fact]
    public async Task A_test_command_ended_at_its_limit_answers_failed_saying_so_and_quoting_what_it_wrote()
    {
        runner.On(c => c.IsShell(ProjectDefaults.DefaultTest), CommandResult.LimitReached("Running 12 tests\n", string.Empty, "Running 12 tests\n"));

        JsonElement result = Result(await PerformAsync(Project, Ids.New()), "failed");

        result.GetProperty("reason").GetString().Should()
            .StartWith("The test command 'dotnet test' was still running after 45 minutes, the limit for one gates command, and was ended.")
            .And.Contain("Running 12 tests");
    }

    [Fact]
    public async Task The_commands_a_project_names_are_the_ones_run()
    {
        await projects.AddAsync(Principal.LocalOwner, "beta", RegisteredProjects.Remote, "main", "make all", "make check", None);

        Result(await PerformAsync("beta", Ids.New()), "done");

        runner.ShellLines.Should().Equal("make all", "make check");
    }

    [Fact]
    public async Task A_failing_build_answers_failed_naming_the_command_and_its_exit_code_and_runs_no_test()
    {
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), new CommandResult(1, "Build FAILED.\n", "error CS1002\n", "error CS1002\nBuild FAILED.\n"));

        JsonElement result = Result(await PerformAsync(Project, Ids.New()), "failed");

        result.GetProperty("reason").GetString().Should()
            .StartWith("The build command 'dotnet build' exited with code 1.")
            .And.Contain("error CS1002\nBuild FAILED.");
        runner.ShellLines.Should().Equal(ProjectDefaults.DefaultBuild);
    }

    [Fact]
    public async Task A_failing_test_after_a_passing_build_answers_failed_naming_the_test_command()
    {
        runner.On(c => c.IsShell(ProjectDefaults.DefaultTest), new CommandResult(127, string.Empty, "sh: dotnet: not found\n", "sh: dotnet: not found\n"));

        JsonElement result = Result(await PerformAsync(Project, Ids.New()), "failed");

        result.GetProperty("reason").GetString().Should().StartWith("The test command 'dotnet test' exited with code 127.").And.Contain("not found");
    }

    [Fact]
    public async Task A_failing_commands_output_is_cut_to_its_last_hundred_lines()
    {
        string output = string.Join('\n', Enumerable.Range(1, 150).Select(n => $"line {n}")) + "\n";
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), new CommandResult(2, output, string.Empty, output));

        string reason = Result(await PerformAsync(Project, Ids.New()), "failed").GetProperty("reason").GetString() ?? string.Empty;

        reason.Should().Contain("The last 100 lines of its output:\nline 51\n").And.EndWith("line 150");
        reason.Should().NotContain("line 50\n");
    }

    [Fact]
    public async Task A_url_with_user_information_in_failing_output_is_stored_without_it()
    {
        const string Output = "fatal: unable to access 'https://bot:s3cret@example.invalid/repo.git/'\nfrom git@example.invalid:team/repo.git\n";
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), new CommandResult(1, Output, string.Empty, Output));

        string reason = Result(await PerformAsync(Project, Ids.New()), "failed").GetProperty("reason").GetString() ?? string.Empty;

        reason.Should().Contain("https://example.invalid/repo.git/").And.Contain("git@example.invalid:team/repo.git").And.NotContain("s3cret");
    }

    [Fact]
    public async Task A_shell_that_cannot_be_started_pauses_with_the_perform_again_lever()
    {
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), (command, _) =>
            Task.FromException<CommandResult>(new CommandNotStartedException(command, "No such file or directory")));

        ActionOutcome.Paused paused = AsPaused(await PerformAsync(Project, Ids.New()));

        paused.Reason.Should().StartWith("Step 'gates' could not start the shell to run the build command 'dotnet build'").And.Contain("No such file or directory");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("gates"));
    }

    [Fact]
    public async Task A_project_with_no_record_pauses_naming_the_step_and_the_project_and_runs_nothing()
    {
        ActionOutcome.Paused paused = AsPaused(await PerformAsync("nowhere", Ids.New()));

        paused.Reason.Should().StartWith("Step 'gates'").And.Contain("project 'nowhere'").And.Contain("Project 'nowhere' is not registered");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("gates"));
        runner.Commands.Should().BeEmpty();
    }

    private ValueTask<ActionOutcome> PerformAsync(string project, Guid flowId) =>
        action.PerformAsync(new ActionRequest(FlowPrincipal, project, flowId, Ids.New(), ShippedContours.Build, Step, Resumed: false), None);

    private static JsonElement Result(ActionOutcome outcome, string verdict)
    {
        JsonElement result = outcome.Should().BeOfType<ActionOutcome.Done>().Subject.Result;
        result.GetProperty("verdict").GetString().Should().Be(verdict);
        result.GetProperty("action").GetString().Should().Be(SoftwareActions.Gates);
        return result;
    }

    private static ActionOutcome.Paused AsPaused(ActionOutcome outcome) =>
        outcome.Should().BeOfType<ActionOutcome.Paused>().Subject;
}
