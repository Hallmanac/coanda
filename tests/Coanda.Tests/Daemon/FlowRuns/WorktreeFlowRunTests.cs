using Coanda.Cli;
using Coanda.Core;
using ProjectDefaults = Coanda.Core.Projects.Project;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Sdk.Commands;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Tests.Daemon.Commands;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The build flow's worktree and gates steps on the scripted command runner: a flow run works in its
/// own worktree, its gates route it to the fix step when they fail, and its worktree is removed when
/// it ends or is stopped, at once or by the next start's sweep.
/// </summary>
public sealed class WorktreeFlowRunTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Failing_gates_send_the_flow_run_to_its_fix_step_and_passing_gates_send_it_on_to_review()
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
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Fix what the gates found");

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");

        Assert.Equal(["worktree", "build", "gates", "fix", "gates", "adversarial-review", "conformance-review", "open-pr", "move-to-review"], flowRun.Steps.Select(s => s.Step));
        Assert.Equal("failed", flowRun.Steps[2].Result.GetValueOrDefault().GetProperty("verdict").GetString());
        Assert.Equal("The build command 'dotnet build' exited with code 1. The last 1 lines of its output:\nerror CS0103", flowRun.Steps[2].Result.GetValueOrDefault().GetProperty("reason").GetString());
        Assert.Equal("done", flowRun.Steps[4].Result.GetValueOrDefault().GetProperty("verdict").GetString());
        Assert.Equal([ProjectDefaults.DefaultBuild, ProjectDefaults.DefaultBuild, ProjectDefaults.DefaultTest], runner.ShellLines);
        Assert.All(runner.Commands.Where(c => c.ShellLine() is not null), c => Assert.True(c.WorkingDirectory == temp.Home.FlowRunWorkPath(Project, flowRun.Id)));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_ended_flow_runs_worktree_is_removed_and_its_branch_kept()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Clean up after the end");

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");
        string workPath = temp.Home.FlowRunWorkPath(Project, flowRun.Id);
        string clone = temp.Home.ProjectClonePath(Project);
        await FlowRunDriver.UntilAsync(() => runner.Worktrees(clone).Count == 0, "the ended flow run's worktree is removed");

        Assert.Contains($"worktree remove --force --force {workPath}", runner.GitCalls);
        // the branch and its commits stay in the clone
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("branch -d", StringComparison.OrdinalIgnoreCase) || c.StartsWith("branch --delete", StringComparison.Ordinal));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_paused_flow_run_keeps_its_worktree_and_a_stop_removes_it()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>> { ["build"] = [Scripts.Answer("blocked", 1)] });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Blocked, then stopped");
        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "paused");
        string workPath = temp.Home.FlowRunWorkPath(Project, paused.Id);
        string clone = temp.Home.ProjectClonePath(Project);

        await Task.Delay(TimeSpan.FromMilliseconds(200), None);
        // a paused flow run keeps its worktree
        Assert.Equal([workPath], runner.Worktrees(clone));

        await FlowRunDriver.StopAsync(client, Project, paused.Id, "Not needed after all.");
        await FlowRunDriver.UntilAsync(() => runner.Worktrees(clone).Count == 0, "the stopped flow run's worktree is removed");

        Assert.Contains($"worktree remove --force --force {workPath}", runner.GitCalls);

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
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Stopped mid-build");
        FlowRunView started = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        await running.Task.WaitAsync(Patience);

        // A pause does not cancel a system step in progress.
        await FlowRunDriver.PauseAsync(client, Project, started.Id, "Look first.");
        await Task.Delay(TimeSpan.FromMilliseconds(200), None);
        Assert.False(ended.Task.IsCompleted, "a pause lets the gates command run on");

        await FlowRunDriver.StopAsync(client, Project, started.Id, "Wrong approach.");
        await ended.Task.WaitAsync(Patience);
        string clone = temp.Home.ProjectClonePath(Project);
        await FlowRunDriver.UntilAsync(() => runner.Worktrees(clone).Count == 0, "the stopped flow run's worktree is removed");

        FlowRunView stopped = await FlowRunDriver.ShowAsync(client, Project, started.Id);
        Assert.Equal("stopped", stopped.Status);
        Assert.Equal(["worktree", "build", "gates"], stopped.Steps.Select(s => s.Step));
        // a stopped system step records nothing
        Assert.Null(stopped.Steps[2].Result);
        // the test command never runs
        Assert.Equal([ProjectDefaults.DefaultBuild], runner.ShellLines);

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
        Guid flowRunId;
        await using (RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner }, None))
        {
            await RegisteredProjects.AddAsync(daemon, Project, None);
            using var client = new DaemonClient(temp.Home);
            ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Daemon stops mid-build");
            flowRunId = (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;
            await running.Task.WaitAsync(Patience);

            await daemon.StopAsync(None);
            Assert.True(ended.Task.IsCompleted, "the daemon's stop ends the command before the stop returns");
        }

        await using RunningDaemon restarted = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = new ScriptedCommandRunner() }, None);
        using var again = new DaemonClient(temp.Home);
        FlowRunView flowRun = await FlowRunDriver.UntilAsync(again, Project, flowRunId, "ended");
        // the step the stop interrupted recorded nothing and was performed again
        Assert.Equal(1, flowRun.Steps.Count(s => s.Step == "gates"));

        await restarted.StopAsync(None);
    }

    [Fact]
    public async Task A_new_flow_run_whose_branch_a_stopped_flow_runs_worktree_holds_takes_it_over_only_once_that_flow_runs_worker_has_returned()
    {
        using var temp = new TemporaryHome();
        var runner = new ScriptedCommandRunner();
        var gatesRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first build command ignores the stop's cancellation, so the stopped flow run's worker stays in its step.
        int builds = 0;
        runner.On(c => c.IsShell(ProjectDefaults.DefaultBuild), async (_, _) =>
        {
            if (Interlocked.Increment(ref builds) == 1)
            {
                gatesRunning.SetResult();
                await release.Task;
            }

            return ScriptedCommandRunner.Ok();
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), CommandRunner = runner }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Take the worktree over");
        Guid first = (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;
        await gatesRunning.Task.WaitAsync(Patience);
        string held = temp.Home.FlowRunWorkPath(Project, first);

        await FlowRunDriver.StopAsync(client, Project, first, "start again");
        await FlowRunDriver.UntilAsync(client, Project, first, "stopped");
        FlowRunView second = await FlowRunDriver.StartAsync(client, Project, parcel.Id, "build");
        string workPath = temp.Home.FlowRunWorkPath(Project, second.Id);
        await FlowRunDriver.UntilAsync(
            () => runner.GitCalls.Count(c => c == "worktree list --porcelain") >= 2,
            "the second flow run's worktree step has looked at the clone's worktrees");
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // nothing is removed while the stopped flow run's worker is in its step
        Assert.DoesNotContain(runner.GitCalls, c => c == $"worktree remove --force --force {held}");
        // nothing is added while the stopped flow run's worker is in its step
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith($"worktree add --quiet {workPath} ", StringComparison.Ordinal));
        Assert.Null((await FlowRunDriver.ShowAsync(client, Project, second.Id)).Steps[0].Result);

        release.SetResult();
        await FlowRunDriver.UntilAsync(
            async () => (await FlowRunDriver.ShowAsync(client, Project, second.Id)).Steps[0].Result is not null,
            "the second flow run's worktree step has recorded its result");

        FlowRunView after = await FlowRunDriver.ShowAsync(client, Project, second.Id);
        Assert.Equal("done", after.Steps[0].Result.GetValueOrDefault().GetProperty("verdict").GetString());
        Assert.Contains(runner.GitCalls, c => c.StartsWith($"worktree add --quiet {workPath} ", StringComparison.Ordinal));
        Assert.DoesNotContain(held, runner.Worktrees(temp.Home.ProjectClonePath(Project)));
        Assert.All((await FlowRunDriver.ShowAsync(client, Project, first)).Steps, s => Assert.True(s.Step != "gates" || s.Result == null, "the stopped flow run records nothing"));

        await daemon.StopAsync(None);
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
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Removal fails");

        FlowRunView flowRun = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "ended");
        await FlowRunDriver.UntilAsync(
            () => log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains($"Removing the worktree of flow run {Ids.Short(flowRun.Id)}", StringComparison.Ordinal)),
            "the failed removal is logged");

        Assert.Equal("pr-opened", flowRun.Outcome);
        // the failed removal left it for the next start's sweep
        Assert.Single(runner.Worktrees(temp.Home.ProjectClonePath(Project)));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_failing_git_command_pauses_the_flow_run_at_the_worktree_step_quoting_its_error_output_without_user_information()
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
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "No network");

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id, "paused");

        Assert.Equal("worktree", paused.CurrentStep);
        Assert.Equal(FlowRunLevers.PerformAgain("worktree"), paused.Lever);
        Assert.StartsWith("Step 'worktree' could not perform the system step 'software/worktree': git --git-dir=", paused.PauseReason, StringComparison.Ordinal);
        Assert.Contains("exited with code 128", paused.PauseReason, StringComparison.Ordinal);
        Assert.Contains("fatal: unable to access 'https://example.invalid/repo.git/': Could not resolve host", paused.PauseReason, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", paused.PauseReason, StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_start_up_sweep_removes_the_worktrees_of_ended_and_stopped_flow_runs_and_leaves_the_rest()
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
        string notAFlowRun = Path.Combine(temp.Home.Path, "work", Project, "scratch");
        string unknownFlowRun = temp.Home.FlowRunWorkPath(Project, Ids.New());
        Guid endedId, stoppedId, pausedId;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, CommandRunner = runner }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            using var client = new DaemonClient(temp.Home);
            endedId = (await FlowRunDriver.UntilAsync(client, Project, await BoundIdAsync(client, "Ends"), "ended")).Id;
            stoppedId = (await FlowRunDriver.UntilAsync(client, Project, await BoundIdAsync(client, "Stops"), "paused")).Id;
            pausedId = (await FlowRunDriver.UntilAsync(client, Project, await BoundIdAsync(client, "Stays paused"), "paused")).Id;
            await FlowRunDriver.StopAsync(client, Project, stoppedId, "Superseded.");
            await FlowRunDriver.UntilAsync(() => runner.GitCalls.Count(c => c.StartsWith("worktree remove", StringComparison.Ordinal)) == 2, "both removals are tried");
            await first.StopAsync(None);
        }

        runner.AddWorktree(clone, notAFlowRun, "scratch", ScriptedCommandRunner.PrimaryHead);
        runner.AddWorktree(clone, unknownFlowRun, "coanda/unknown", ScriptedCommandRunner.PrimaryHead);
        Assert.Equal(5, runner.Worktrees(clone).Count);
        Volatile.Write(ref removalsFail, false);

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor, CommandRunner = runner }, None);

        Assert.Equivalent(new[] { temp.Home.FlowRunWorkPath(Project, pausedId), notAFlowRun, unknownFlowRun }, runner.Worktrees(clone), strict: true);
        Assert.Contains($"worktree remove --force --force {temp.Home.FlowRunWorkPath(Project, endedId)}", runner.GitCalls);
        Assert.Contains($"worktree remove --force --force {temp.Home.FlowRunWorkPath(Project, stoppedId)}", runner.GitCalls);

        await second.StopAsync(None);
    }

    private static async Task<Guid> BoundIdAsync(DaemonClient client, string title)
    {
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, title);
        return (await FlowRunDriver.BoundAsync(client, Project, parcel.Id)).Id;
    }
}
