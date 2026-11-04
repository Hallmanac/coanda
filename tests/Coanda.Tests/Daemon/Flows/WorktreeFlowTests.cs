using Coanda.Cli;
using Coanda.Core;
using ProjectDefaults = Coanda.Core.Projects.Project;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Commands;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Flows;
using Coanda.Tests.Daemon.Commands;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>
/// The build contour's worktree and gates steps on the scripted command runner: a flow works in its
/// own worktree, its gates route it to the fix step when they fail, and its worktree is removed when
/// it ends or is stopped, at once or by the next start's sweep.
/// </summary>
public sealed class WorktreeFlowTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Failing_gates_send_the_flow_to_its_fix_step_and_passing_gates_send_it_on_to_review()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        int builds = 0;
        runner.On(
            c => c.IsShell(ProjectDefaults.DefaultBuild),
            (_, _) => Task.FromResult(Interlocked.Increment(ref builds) == 1
                ? new CommandResult(1, string.Empty, "error CS0103\n", "error CS0103\n")
                : ScriptedCommandRunner.Ok()));
        Dictionary<string, IReadOnlyList<ScriptEntry>> script = Scripts.CleanBuild();
        script["fix"] = [Scripts.Answer("done", 4)];
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = new ScriptedExecutor(script), CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Fix what the gates found");

        FlowView flow = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        flow.Steps.Select(s => s.Step).Should().Equal(
            "worktree", "build", "gates", "fix", "gates", "adversarial-review", "conformance-review", "open-pr", "move-to-review");
        flow.Steps[2].Result.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("failed");
        flow.Steps[2].Result.GetValueOrDefault().GetProperty("reason").GetString().Should()
            .Be("The build command 'dotnet build' exited with code 1. The last 1 lines of its output:\nerror CS0103");
        flow.Steps[4].Result.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("done");
        runner.ShellLines.Should().Equal(ProjectDefaults.DefaultBuild, ProjectDefaults.DefaultBuild, ProjectDefaults.DefaultTest);
        runner.Commands.Where(c => c.ShellLine() is not null).Should().OnlyContain(c => c.WorkingDirectory == temp.Home.FlowWorkPath(Project, flow.Id));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_ended_flows_worktree_is_removed_and_its_branch_kept()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Clean up after the end");

        FlowView flow = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "ended");
        string workPath = temp.Home.FlowWorkPath(Project, flow.Id);
        string clone = temp.Home.ProjectClonePath(Project);
        await Flowing.UntilAsync(() => runner.Worktrees(clone).Count == 0, "the ended flow's worktree is removed");

        runner.GitCalls.Should().Contain($"worktree remove --force --force {workPath}");
        runner.GitCalls.Should().NotContain(
            c => c.StartsWith("branch -d", StringComparison.OrdinalIgnoreCase) || c.StartsWith("branch --delete", StringComparison.Ordinal),
            "the branch and its commits stay in the clone");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_paused_flow_keeps_its_worktree_and_a_stop_removes_it()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>> { ["build"] = [Scripts.Answer("blocked", 1)] });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Blocked, then stopped");
        FlowView paused = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "paused");
        string workPath = temp.Home.FlowWorkPath(Project, paused.Id);
        string clone = temp.Home.ProjectClonePath(Project);

        await Task.Delay(TimeSpan.FromMilliseconds(200), None);
        runner.Worktrees(clone).Should().Equal([workPath], "a paused flow keeps its worktree");

        await Flowing.StopAsync(client, Project, paused.Id, "Not needed after all.");
        await Flowing.UntilAsync(() => runner.Worktrees(clone).Count == 0, "the stopped flow's worktree is removed");

        runner.GitCalls.Should().Contain($"worktree remove --force --force {workPath}");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_stop_while_a_gates_command_runs_ends_that_command_records_nothing_and_then_removes_the_worktree()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), async (_, token) =>
        {
            running.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                ended.TrySetResult();
            }

            return ScriptedCommandRunner.Ok();
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Stopped mid-build");
        FlowView started = await Flowing.BoundAsync(client, Project, parcel.Id);
        await running.Task.WaitAsync(Patience);

        // A pause does not cancel an action in progress.
        await Flowing.PauseAsync(client, Project, started.Id, "Look first.");
        await Task.Delay(TimeSpan.FromMilliseconds(200), None);
        ended.Task.IsCompleted.Should().BeFalse("a pause lets the gates command run on");

        await Flowing.StopAsync(client, Project, started.Id, "Wrong approach.");
        await ended.Task.WaitAsync(Patience);
        string clone = temp.Home.ProjectClonePath(Project);
        await Flowing.UntilAsync(() => runner.Worktrees(clone).Count == 0, "the stopped flow's worktree is removed");

        FlowView stopped = await Flowing.ShowAsync(client, Project, started.Id);
        stopped.Status.Should().Be("stopped");
        stopped.Steps.Select(s => s.Step).Should().Equal("worktree", "build", "gates");
        stopped.Steps[2].Result.Should().BeNull("a stopped action records nothing");
        runner.ShellLines.Should().Equal([ProjectDefaults.DefaultBuild], "the test command never runs");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Stopping_the_daemon_while_a_gates_command_runs_ends_the_command_and_leaves_the_step_to_perform_again()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), async (_, token) =>
        {
            running.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                ended.TrySetResult();
            }

            return ScriptedCommandRunner.Ok();
        });
        Guid flowId;
        await using (RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner }, None))
        {
            await RegisteredProjects.AddAsync(daemon, Project, None);
            using var client = new DaemonClient(temp.Home);
            ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Daemon stops mid-build");
            flowId = (await Flowing.BoundAsync(client, Project, parcel.Id)).Id;
            await running.Task.WaitAsync(Patience);

            await daemon.StopAsync(None);
            ended.Task.IsCompleted.Should().BeTrue("the daemon's stop ends the command before the stop returns");
        }

        await using RunningDaemon restarted = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = new ScriptedCommandRunner() }, None);
        using var again = new DaemonClient(temp.Home);
        FlowView flow = await Flowing.UntilAsync(again, Project, flowId, "ended");
        flow.Steps.Count(s => s.Step == "gates").Should().Be(1, "the step the stop interrupted recorded nothing and was performed again");

        await restarted.StopAsync(None);
    }

    [Fact]
    public async Task A_removal_that_fails_is_logged_and_the_end_is_still_recorded()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        runner.On(c => c.IsGit("worktree", "remove"), ScriptedCommandRunner.Fails(128, "fatal: cannot remove a locked working tree"));
        var log = new RecordingLogger();
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner, LoggerProvider = log }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Removal fails");

        FlowView flow = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "ended");
        await Flowing.UntilAsync(
            () => log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains($"Removing the worktree of flow {Ids.Short(flow.Id)}", StringComparison.Ordinal)),
            "the failed removal is logged");

        flow.Outcome.Should().Be("pr-opened");
        runner.Worktrees(temp.Home.ProjectClonePath(Project)).Should().ContainSingle("the failed removal left it for the next start's sweep");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_failing_git_command_pauses_the_flow_at_the_worktree_step_quoting_its_error_output_without_user_information()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        runner.On(c => c.IsGit("fetch"), ScriptedCommandRunner.Fails(
            128,
            "fatal: unable to access 'https://bot:s3cret@example.invalid/repo.git/': Could not resolve host\n"));
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "No network");

        FlowView paused = await Flowing.UntilAsync(client, Project, (await Flowing.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        paused.CurrentStep.Should().Be("worktree");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("worktree"));
        paused.PauseReason.Should().StartWith("Step 'worktree' could not perform the action 'worktree': git --git-dir=")
            .And.Contain("exited with code 128")
            .And.Contain("fatal: unable to access 'https://example.invalid/repo.git/': Could not resolve host")
            .And.NotContain("s3cret");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_start_up_sweep_removes_the_worktrees_of_ended_and_stopped_flows_and_leaves_the_rest()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        bool removalsFail = true;
        runner.On(c => c.IsGit("worktree", "remove") && Volatile.Read(ref removalsFail), ScriptedCommandRunner.Fails(1, "fatal: busy"));
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1), Scripts.Answer("blocked", 2), Scripts.Answer("blocked", 3)],
            ["reviewer-adversarial"] = [Scripts.Answer("clean", 4)],
            ["reviewer-conformance"] = [Scripts.Answer("clean", 5)],
        });
        string clone = temp.Home.ProjectClonePath(Project);
        string notAFlow = Path.Combine(temp.Home.Path, "work", Project, "scratch");
        string unknownFlow = temp.Home.FlowWorkPath(Project, Ids.New());
        Guid endedId, stoppedId, pausedId;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, CommandRunner = runner }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            using var client = new DaemonClient(temp.Home);
            endedId = (await Flowing.UntilAsync(client, Project, await BoundIdAsync(client, "Ends"), "ended")).Id;
            stoppedId = (await Flowing.UntilAsync(client, Project, await BoundIdAsync(client, "Stops"), "paused")).Id;
            pausedId = (await Flowing.UntilAsync(client, Project, await BoundIdAsync(client, "Stays paused"), "paused")).Id;
            await Flowing.StopAsync(client, Project, stoppedId, "Superseded.");
            await Flowing.UntilAsync(() => runner.GitCalls.Count(c => c.StartsWith("worktree remove", StringComparison.Ordinal)) == 2, "both removals are tried");
            await first.StopAsync(None);
        }

        runner.AddWorktree(clone, notAFlow, "scratch", ScriptedCommandRunner.PrimaryHead);
        runner.AddWorktree(clone, unknownFlow, "coanda/unknown", ScriptedCommandRunner.PrimaryHead);
        runner.Worktrees(clone).Should().HaveCount(5);
        Volatile.Write(ref removalsFail, false);

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, CommandRunner = runner }, None);

        runner.Worktrees(clone).Should().BeEquivalentTo([temp.Home.FlowWorkPath(Project, pausedId), notAFlow, unknownFlow]);
        runner.GitCalls.Should().Contain($"worktree remove --force --force {temp.Home.FlowWorkPath(Project, endedId)}")
            .And.Contain($"worktree remove --force --force {temp.Home.FlowWorkPath(Project, stoppedId)}");

        await second.StopAsync(None);
    }

    private static async Task<Guid> BoundIdAsync(DaemonClient client, string title)
    {
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, title);
        return (await Flowing.BoundAsync(client, Project, parcel.Id)).Id;
    }
}
