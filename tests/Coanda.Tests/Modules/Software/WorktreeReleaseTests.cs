using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.FlowRuns;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// The software module releases a flow run's worktree on the flow run's ended or stopped feed delivery:
/// a delivery that finds nothing to remove removes nothing and logs no failure, and the worktree step
/// takes a holding worktree over once the holder's delivery has reached the module, waiting for it when
/// it has not and not otherwise.
/// </summary>
public sealed class WorktreeReleaseTests : IAsyncLifetime
{
    private const string Project = SoftwareNode.Project;
    private const string Uncloned = "gamma";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly ScriptedCommandRunner runner = new();
    private readonly RecordingLogger log = new();
    private SoftwareNode node = null!;

    private string Clone => node.Home.ProjectClonePath(Project);

    public async Task InitializeAsync() => node = await SoftwareNode.StartAsync(runner, log: log, projects: [Project, Uncloned]);

    public async Task DisposeAsync() => await node.DisposeAsync();

    [Fact]
    public void The_module_subscribes_to_the_ends_and_stops_of_flow_runs_and_nothing_else()
    {
        Assert.Equal([CoreEventKind.FlowRunEnded, CoreEventKind.FlowRunStopped], SoftwareDescribe.Answer.Subscriptions);
    }

    [Fact]
    public async Task A_delivery_repeated_for_a_worktree_already_removed_removes_nothing_and_logs_no_failure()
    {
        Parcel parcel = await node.TaskAsync("Ended twice");
        (Guid ended, _) = await node.HeldAsync(parcel.Id, "worktree");
        Assert.IsType<SystemStepAnswer.Done>(await node.DispatchAsync(SoftwareSteps.Worktree, parcel.Id, ended));
        await node.DeliverEndAsync(ended);
        Assert.Empty(runner.Worktrees(Clone));
        int before = runner.Commands.Count;

        await node.DeliverEndAsync(ended);

        Assert.DoesNotContain(runner.GitCalls.Skip(before), c => c.StartsWith("worktree remove", StringComparison.Ordinal) || c.StartsWith("worktree prune", StringComparison.Ordinal));
        AssertNoFailureLogged();
    }

    [Fact]
    public async Task A_delivery_for_a_project_whose_clone_is_gone_runs_nothing_and_logs_no_failure()
    {
        Parcel parcel = await node.TaskAsync("Uncloned", Uncloned);
        (Guid ended, _) = await node.HeldAsync(parcel.Id, "worktree", Uncloned);
        Directory.Delete(node.Home.ProjectClonePath(Uncloned));

        await node.DeliverEndAsync(ended, Uncloned);

        Assert.Empty(runner.Commands);
        AssertNoFailureLogged();
    }

    [Fact]
    public async Task A_delivery_for_a_flow_run_the_core_holds_no_record_of_runs_nothing_and_logs_no_failure()
    {
        await node.DeliverEndAsync(Ids.New());

        Assert.Empty(runner.Commands);
        AssertNoFailureLogged();
    }

    [Fact]
    public async Task A_delivery_removes_the_worktree_at_the_work_path_the_core_shows_and_no_other()
    {
        (_, string held, Guid holder) = await HeldAsync();
        string elsewhere = Path.Combine(node.Home.Path, "elsewhere", holder.ToString());
        runner.AddWorktree(Clone, elsewhere, "coanda/elsewhere", ScriptedCommandRunner.PrimaryHead);

        await node.DeliverEndAsync(holder);

        // a worktree whose last segment names the flow run, at a path that is not its work path, is left
        Assert.Equal([elsewhere], runner.Worktrees(Clone));
        Assert.DoesNotContain(held, runner.Worktrees(Clone));
    }

    [Fact]
    public async Task A_clone_whose_listing_fails_does_not_keep_the_delivery_from_the_other_clones_and_its_failure_is_logged()
    {
        (_, string held, Guid holder) = await HeldAsync();
        // "another" sorts before the project's clone, so its failure comes first.
        string another = node.Home.ProjectClonePath(Project, "another");
        Directory.CreateDirectory(another);
        runner.On(
            c => c.IsGit("worktree", "list") && c.GitDirectory() == another,
            ScriptedCommandRunner.Fails(128, "fatal: not a git repository"));

        await node.DeliverEndAsync(holder);

        Assert.DoesNotContain(held, runner.Worktrees(Clone));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains($"Removing the worktree of flow run {Ids.Short(holder)}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_takeover_waits_for_the_holders_stopped_delivery_and_that_delivery_releases_it()
    {
        (Guid parcelId, string held, Guid holder) = await HeldAsync();
        // The daemon's feed holds the stop while the holder's worker is running, as it would be mid-step.
        node.Workers.Started(holder, new TaskCompletionSource().Task);
        await node.StopAsync(holder);
        Guid taker = Ids.New();
        int before = runner.Commands.Count;

        Task<SystemStepAnswer> takeover = node.DispatchAsync(SoftwareSteps.Worktree, parcelId, taker);
        await FlowRunDriver.UntilAsync(() => runner.GitCalls.Skip(before).Any(c => c == "worktree list --porcelain"), "the takeover has looked at the worktrees");
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(takeover.IsCompleted, "the takeover waits for the holder's delivery");

        await node.DeliverEndAsync(holder);

        Assert.IsType<SystemStepAnswer.Done>(await takeover.WaitAsync(Patience));
        Assert.Equal([node.Home.FlowRunWorkPath(Project, taker)], runner.Worktrees(Clone));
        Assert.DoesNotContain(held, runner.Worktrees(Clone));
    }

    [Fact]
    public async Task A_holder_whose_removal_failed_after_its_delivery_is_taken_over_without_waiting_and_its_later_delivery_removes_nothing()
    {
        (Guid parcelId, string held, Guid holder) = await HeldAsync();
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Workers.Started(holder, working.Task);
        await node.StopAsync(holder);
        int removals = 0;
        runner.On(
            c => c.IsGit("worktree", "remove") && Interlocked.Increment(ref removals) == 1,
            ScriptedCommandRunner.Fails(128, "fatal: cannot remove a locked working tree"));
        // The holder's end reaches the module, and its removal fails.
        await node.DeliverEndAsync(holder);
        Assert.Contains(held, runner.Worktrees(Clone));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains($"Removing the worktree of flow run {Ids.Short(holder)}", StringComparison.Ordinal));
        Guid taker = Ids.New();

        Assert.IsType<SystemStepAnswer.Done>(await node.DispatchAsync(SoftwareSteps.Worktree, parcelId, taker).WaitAsync(Patience));

        Assert.Equal([node.Home.FlowRunWorkPath(Project, taker)], runner.Worktrees(Clone));

        // The daemon's feed now delivers the stop once the worker returns; the worktree is already the taker's.
        int before = runner.Commands.Count;
        node.Workers.Returned(holder);
        working.SetResult();
        await FlowRunDriver.UntilAsync(() => runner.GitCalls.Skip(before).Any(c => c == "worktree list --porcelain"), "the feed's delivery of the stop has looked at the worktrees");
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.DoesNotContain(runner.GitCalls.Skip(before), c => c.StartsWith("worktree remove", StringComparison.Ordinal) || c.StartsWith("worktree prune", StringComparison.Ordinal));
        Assert.Equal([node.Home.FlowRunWorkPath(Project, taker)], runner.Worktrees(Clone));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
    }

    /// <summary>A task whose branch a live flow run of its own has checked out at that flow run's work path.</summary>
    private async Task<(Guid ParcelId, string Held, Guid Holder)> HeldAsync()
    {
        Parcel parcel = await node.TaskAsync("Held");
        (Guid holder, _) = await node.HeldAsync(parcel.Id, "worktree");
        Assert.IsType<SystemStepAnswer.Done>(await node.DispatchAsync(SoftwareSteps.Worktree, parcel.Id, holder));
        string held = node.Home.FlowRunWorkPath(Project, holder);
        Assert.Contains(held, runner.Worktrees(Clone));
        return (parcel.Id, held, holder);
    }

    private void AssertNoFailureLogged() =>
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
}
