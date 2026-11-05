using System.Text.Json;
using Coanda.Core;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Client;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.FlowRuns;

namespace Coanda.Tests.Modules.Software.Steps;

/// <summary>
/// The worktree step against the scripted command runner's model of the node's clone. A dispatch whose
/// answer is a pause runs for a flow run sitting in a worktree step, on a parcel of its own, so the
/// reason and the lever name that step.
/// </summary>
public sealed class WorktreeStepTests : IAsyncLifetime
{
    private const string Project = SoftwareNode.Project;
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A flow whose one step is open-pr, so a flow run of it can hold a recorded push.</summary>
    private static readonly Flow Recorder = new()
    {
        Name = "recorder",
        Accepts = ["task"],
        Outcome = "recorded",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            new FlowStep
            {
                Name = "open-pr",
                SystemStep = SoftwareSteps.OpenPr,
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("opened"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    private readonly ScriptedCommandRunner runner = new();
    private SoftwareNode node = null!;
    private ParcelService parcels = null!;
    private FlowRunService flowRuns = null!;

    private string Clone => node.Home.Clones().ProjectClonePath(Project);

    public async Task InitializeAsync()
    {
        node = await SoftwareNode.StartAsync(runner);
        await Publishing.FlowAsync(node.Daemon, Project, Recorder);
        parcels = node.Parcels;
        flowRuns = node.FlowRuns;
    }

    public async Task DisposeAsync() => await node.DisposeAsync();

    [Fact]
    public async Task A_first_run_clones_the_project_cuts_the_parcels_branch_from_the_fetched_primary_and_adds_the_worktree()
    {
        Guid parcelId = Ids.New();
        Guid flowRunId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        string workPath = node.Home.FlowRunWorkPath(Project, flowRunId);
        string partial = node.Home.Clones().ProjectClonePartialPath(Project);

        JsonElement result = Done(await DispatchAsync(parcelId, flowRunId));

        Assert.Equal(branch, result.GetProperty("branch").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, result.GetProperty("head").GetString());
        Assert.Equal([$"init --bare --quiet {partial}", $"remote add origin {RegisteredProjects.Remote}", "worktree list --porcelain", $"for-each-ref --format=%(objectname) refs/heads/{branch}", $"fetch --no-tags --quiet origin +refs/heads/{branch}:{ProjectClones.TrackingRefOf(branch)}", $"update-ref -d {ProjectClones.TrackingRefOf(branch)}", $"fetch --no-tags --quiet origin +refs/heads/{RegisteredProjects.Branch}:refs/remotes/origin/{RegisteredProjects.Branch}", $"rev-parse --verify --quiet refs/remotes/origin/{RegisteredProjects.Branch}^{{commit}}", $"ls-tree -z --name-only {ScriptedCommandRunner.PrimaryHead}", $"config {ProjectClones.OwnerKeyOf(branch)} {parcelId}", $"branch --no-track {branch} {ScriptedCommandRunner.PrimaryHead}", $"worktree add --quiet {workPath} {branch}"], runner.GitCalls);
        Assert.All(runner.Commands.Skip(1), c => Assert.True(c.GitDirectory() == Clone || c.GitDirectory() == partial));
        Assert.Equal(parcelId.ToString(), runner.Config(Clone, ProjectClones.OwnerKeyOf(branch)));
        Assert.True(Directory.Exists(Clone), "the clone is moved into place once it is made");
        Assert.False(Directory.Exists(partial));
        Assert.Single(ExcludeLines(), l => l == ProjectClones.ExcludeLine);
    }

    [Fact]
    public async Task Every_git_command_runs_with_terminal_prompts_off_ssh_in_batch_mode_and_the_C_locale()
    {
        await DispatchAsync(Ids.New(), Ids.New());

        Assert.NotEmpty(runner.Commands);
        Assert.All(runner.Commands, c => Assert.True(c.Program == "git"
            && c.Environment["GIT_TERMINAL_PROMPT"] == "0"
            && c.Environment["GIT_SSH_COMMAND"].Contains("BatchMode=yes")
            && c.Environment["LC_ALL"] == "C"));
    }

    [Fact]
    public async Task A_repeat_run_on_a_work_path_that_holds_the_worktree_answers_its_head_and_fetches_creates_and_checks_out_nothing()
    {
        Guid parcelId = Ids.New();
        Guid flowRunId = Ids.New();
        await DispatchAsync(parcelId, flowRunId);
        DateTime written = File.GetLastWriteTimeUtc(ExcludePath);
        int before = runner.Commands.Count;

        JsonElement again = Done(await DispatchAsync(parcelId, flowRunId));

        Assert.Equal($"coanda/{Ids.Short(parcelId)}", again.GetProperty("branch").GetString());
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, again.GetProperty("head").GetString());
        Assert.Equal(["rev-parse --is-bare-repository", "worktree list --porcelain"], runner.GitCalls.Skip(before));
        Assert.Single(ExcludeLines(), l => l == ProjectClones.ExcludeLine);
        // the exclude line is written only when it is missing
        Assert.Equal(written, File.GetLastWriteTimeUtc(ExcludePath));
    }

    [Fact]
    public async Task A_branch_an_earlier_flow_run_on_the_parcel_created_is_checked_out_at_its_own_tip_when_the_remote_has_no_branch_of_that_name()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await DispatchAsync(parcelId, earlier);
        await EndAsync(earlier);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), Moved);
        int before = runner.Commands.Count;
        Guid later = Ids.New();

        JsonElement result = Done(await DispatchAsync(parcelId, later));

        // the branch is checked out at its own tip, not the primary's
        Assert.Equal(Moved, result.GetProperty("head").GetString());
        Assert.Equal(["rev-parse --is-bare-repository", "worktree list --porcelain", $"for-each-ref --format=%(objectname) refs/heads/{branch}", $"config --get --default  {ProjectClones.OwnerKeyOf(branch)}", $"fetch --no-tags --quiet origin +refs/heads/{branch}:{ProjectClones.TrackingRefOf(branch)}", $"update-ref -d {ProjectClones.TrackingRefOf(branch)}", $"ls-tree -z --name-only {Moved}", $"worktree add --quiet {node.Home.FlowRunWorkPath(Project, later)} {branch}"], runner.GitCalls.Skip(before));
        // someone deleted the branch on the remote, so the next push's lease expects it absent
        Assert.Null(runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
    }

    [Fact]
    public async Task A_branch_the_remote_has_moved_ahead_of_moves_forward_to_the_remotes_commit_before_the_worktree_is_added()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await DispatchAsync(parcelId, earlier);
        await EndAsync(earlier);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);
        runner.AddAncestry(Moved, Ahead);
        Guid later = Ids.New();

        JsonElement result = Done(await DispatchAsync(parcelId, later));

        // a commit someone added on the remote is built on
        Assert.Equal(Ahead, result.GetProperty("head").GetString());
        Assert.Equal(Ahead, runner.Branch(Clone, branch));
        // the next push's lease expects what the remote holds now
        Assert.Equal(Ahead, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
        SequenceAssert.ContainsInOrder(
            [
                $"update-ref refs/heads/{branch} {Ahead} {Moved}",
                $"ls-tree -z --name-only {Ahead}",
                $"worktree add --quiet {node.Home.FlowRunWorkPath(Project, later)} {branch}",
            ],
            runner.GitCalls);
    }

    [Fact]
    public async Task A_branch_ahead_of_the_remotes_stays_at_its_own_tip_and_the_lease_expects_the_remotes()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await DispatchAsync(parcelId, earlier);
        await EndAsync(earlier);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, ScriptedCommandRunner.PrimaryHead);
        runner.AddAncestry(ScriptedCommandRunner.PrimaryHead, Moved);

        JsonElement result = Done(await DispatchAsync(parcelId, Ids.New()));

        Assert.Equal(Moved, result.GetProperty("head").GetString());
        Assert.Equal(Moved, runner.Branch(Clone, branch));
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith($"update-ref refs/heads/{branch}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_that_has_diverged_from_the_remotes_pauses_naming_how_to_reconcile_them_and_is_neither_moved_nor_checked_out()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await DispatchAsync(parcelId, earlier);
        await EndAsync(earlier);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);
        int before = runner.Commands.Count;

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcelId, await AtWorktreeAsync()));

        Assert.Contains($"Branch {branch}", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(Moved, paused.Reason, StringComparison.Ordinal);
        Assert.Contains(Ahead, paused.Reason, StringComparison.Ordinal);
        Assert.Contains("Reconcile them by hand", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(Clone, paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("worktree"), paused.Lever);
        Assert.Equal(Moved, runner.Branch(Clone, branch));
        Assert.DoesNotContain(runner.GitCalls.Skip(before), c => c.StartsWith("worktree add", StringComparison.Ordinal) || c.StartsWith("update-ref", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_the_clone_does_not_hold_but_the_remote_does_is_cut_from_the_remotes_branch_rather_than_the_primary()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await DispatchAsync(Ids.New(), Ids.New());
        runner.SetRemoteBranch(Clone, branch, Moved);
        int before = runner.Commands.Count;

        JsonElement result = Done(await DispatchAsync(parcelId, Ids.New()));

        Assert.Equal(Moved, result.GetProperty("head").GetString());
        Assert.Equal(Moved, runner.Branch(Clone, branch));
        Assert.Equal(Moved, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
        IEnumerable<string> actual = runner.GitCalls.Skip(before);
        Assert.Contains($"branch --no-track {branch} {Moved}", actual);
        Assert.DoesNotContain(actual, c => c.StartsWith($"fetch --no-tags --quiet origin +refs/heads/{RegisteredProjects.Branch}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_a_live_flow_runs_worktree_has_checked_out_pauses_naming_that_flow_run_and_is_neither_fetched_nor_moved()
    {
        (Guid parcelId, string branch, Guid holder, string held) = await HeldByFlowRunAsync();
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);
        runner.AddAncestry(Moved, Ahead);
        int before = runner.Commands.Count;

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcelId, await AtWorktreeAsync()));

        Assert.Contains($"flow run {holder}", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("still live", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(held, paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("worktree"), paused.Lever);
        Assert.DoesNotContain(runner.GitCalls.Skip(before), c =>
            c.StartsWith("fetch ", StringComparison.Ordinal) || c.StartsWith("update-ref", StringComparison.Ordinal) || c.StartsWith("worktree remove", StringComparison.Ordinal) || c.StartsWith("worktree add", StringComparison.Ordinal));
        Assert.Equal(Moved, runner.Branch(Clone, branch));
    }

    [Fact]
    public async Task A_branch_held_by_a_worktree_at_a_path_that_is_no_flow_runs_work_path_pauses_naming_that_path()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await DispatchAsync(Ids.New(), Ids.New());
        runner.AddBranch(Clone, branch, Moved);
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), parcelId.ToString());
        string mine = Path.Combine(node.Home.ClonesPath, "someones-own-worktree");
        runner.AddWorktree(Clone, mine, branch, Moved);

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcelId, await AtWorktreeAsync()));

        Assert.Contains(mine, paused.Reason, StringComparison.Ordinal);
        Assert.Contains("no work path of a flow run the node holds", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("worktree"), paused.Lever);
        // nothing removes a worktree whose flow run is unknown
        Assert.Contains(mine, runner.Worktrees(Clone));
    }

    [Fact]
    public async Task A_branch_held_by_the_worktree_of_a_flow_run_the_store_does_not_hold_pauses_naming_that_path()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await DispatchAsync(Ids.New(), Ids.New());
        runner.AddBranch(Clone, branch, Moved);
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), parcelId.ToString());
        string unknown = node.Home.FlowRunWorkPath(Project, Ids.New());
        runner.AddWorktree(Clone, unknown, branch, Moved);

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcelId, await AtWorktreeAsync()));

        Assert.Contains(unknown, paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("worktree"), paused.Lever);
        Assert.Contains(unknown, runner.Worktrees(Clone));
    }

    [Fact]
    public async Task A_branch_held_by_a_stopped_flow_runs_worktree_is_taken_over_removing_that_worktree_and_checking_the_branch_out_here()
    {
        (Guid parcelId, string branch, Guid holder, string held) = await HeldByFlowRunAsync();
        await flowRuns.StopAsync(Principal.LocalOwner, Project, holder, "the build is done with it", None);
        Guid taker = Ids.New();
        string workPath = node.Home.FlowRunWorkPath(Project, taker);
        int before = runner.Commands.Count;

        JsonElement result = Done(await DispatchAsync(parcelId, taker));

        Assert.Equal(branch, result.GetProperty("branch").GetString());
        SequenceAssert.ContainsInOrder(
            [
                $"worktree remove --force --force {held}",
                "worktree list --porcelain",
                $"fetch --no-tags --quiet origin +refs/heads/{branch}:{ProjectClones.TrackingRefOf(branch)}",
                $"worktree add --quiet {workPath} {branch}",
            ],
            runner.GitCalls.Skip(before));
        Assert.Equal([workPath], runner.Worktrees(Clone));

        // A removal of the stopped flow run's worktree that runs afterwards finds nothing at its work path.
        before = runner.Commands.Count;
        await node.DeliverEndAsync(holder);
        Assert.DoesNotContain(runner.GitCalls.Skip(before), c => c.StartsWith("worktree remove", StringComparison.Ordinal) || c.StartsWith("worktree prune", StringComparison.Ordinal));
        Assert.Equal([workPath], runner.Worktrees(Clone));
    }

    [Fact]
    public async Task A_takeover_whose_holding_worktree_was_already_removed_answers_done()
    {
        (Guid parcelId, string branch, Guid holder, string held) = await HeldByFlowRunAsync();
        await flowRuns.StopAsync(Principal.LocalOwner, Project, holder, "the build is done with it", None);
        // the stopped flow run's own removal runs first
        await FlowRunDriver.UntilAsync(() => !runner.Worktrees(Clone).Contains(held), "the feed's delivery of the stop removes the holding worktree");
        Guid taker = Ids.New();

        JsonElement result = Done(await DispatchAsync(parcelId, taker));

        Assert.Equal(branch, result.GetProperty("branch").GetString());
        Assert.Equal([node.Home.FlowRunWorkPath(Project, taker)], runner.Worktrees(Clone));
    }

    [Fact]
    public async Task A_takeover_from_a_stopped_flow_run_whose_worker_is_still_performing_removes_and_adds_nothing_until_that_worker_returns()
    {
        (Guid parcelId, string branch, Guid holder, string held) = await HeldByFlowRunAsync();
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.Workers.Started(holder, working.Task);
        await flowRuns.StopAsync(Principal.LocalOwner, Project, holder, "the build is done with it", None);
        Guid taker = Ids.New();
        int before = runner.Commands.Count;

        Task<SystemStepAnswer> takeover = DispatchAsync(parcelId, taker);
        await FlowRunDriver.UntilAsync(() => runner.GitCalls.Skip(before).Any(c => c == "worktree list --porcelain"), "the takeover has listed the worktrees");
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.False(takeover.IsCompleted, "the takeover waits for the stopped flow run's worker");
        Assert.DoesNotContain(runner.GitCalls.Skip(before), c => c.StartsWith("worktree remove", StringComparison.Ordinal) || c.StartsWith("worktree add", StringComparison.Ordinal));
        // the clone's turn is not held while the takeover waits
        await node.DeliverEndAsync(Ids.New()).WaitAsync(TimeSpan.FromSeconds(15));

        node.Workers.Returned(holder);
        working.SetResult();
        Done(await takeover.WaitAsync(TimeSpan.FromSeconds(15)));
        IEnumerable<string> actual = runner.GitCalls.Skip(before);
        Assert.Contains($"worktree remove --force --force {held}", actual);
        Assert.Contains($"worktree add --quiet {node.Home.FlowRunWorkPath(Project, taker)} {branch}", actual);
    }

    [Fact]
    public async Task A_takeover_waiting_on_a_worker_ends_when_its_own_step_is_cancelled()
    {
        (Guid parcelId, _, Guid holder, _) = await HeldByFlowRunAsync();
        node.Workers.Started(holder, new TaskCompletionSource().Task);
        await flowRuns.StopAsync(Principal.LocalOwner, Project, holder, "the build is done with it", None);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Func<Task> takeover = async () => await node.DispatchAsync(SoftwareSteps.Worktree, parcelId, Ids.New(), cancellationToken: stop.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(takeover);
    }

    [Fact]
    public async Task A_branch_rewritten_in_the_clone_while_the_remote_still_holds_the_last_recorded_push_is_checked_out_as_it_stands()
    {
        Guid parcelId = await TaskAsync();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await DispatchAsync(parcelId, earlier);
        await EndAsync(earlier);
        await RecordedPushAsync(parcelId, Ahead);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);
        int before = runner.Commands.Count;
        Guid later = Ids.New();

        JsonElement result = Done(await DispatchAsync(parcelId, later));

        // the clone's branch is checked out as it stands
        Assert.Equal(Moved, result.GetProperty("head").GetString());
        Assert.Equal(Moved, runner.Branch(Clone, branch));
        // the push's lease expects what the remote holds
        Assert.Equal(Ahead, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
        IEnumerable<string> actual = runner.GitCalls.Skip(before);
        Assert.Contains($"worktree add --quiet {node.Home.FlowRunWorkPath(Project, later)} {branch}", actual);
        Assert.DoesNotContain(actual, c => c.StartsWith($"update-ref refs/heads/{branch}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_diverged_from_a_remote_holding_a_commit_nothing_recorded_is_still_refused()
    {
        Guid parcelId = await TaskAsync();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await DispatchAsync(parcelId, earlier);
        await EndAsync(earlier);
        await RecordedPushAsync(parcelId, ScriptedCommandRunner.PrimaryHead);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcelId, await AtWorktreeAsync()));

        Assert.Contains("Reconcile them by hand", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("not the commit last recorded as pushed", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(Moved, runner.Branch(Clone, branch));
    }

    [Fact]
    public async Task An_existing_branch_another_parcel_owns_or_no_parcel_owns_pauses_naming_the_branch_and_is_not_checked_out()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await DispatchAsync(Ids.New(), Ids.New());
        runner.AddBranch(Clone, branch, ScriptedCommandRunner.PrimaryHead);

        SystemStepAnswer.Paused unowned = Paused(await DispatchAsync(parcelId, await AtWorktreeAsync()));
        Guid other = Ids.New();
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), other.ToString());
        SystemStepAnswer.Paused owned = Paused(await DispatchAsync(parcelId, await AtWorktreeAsync()));

        Assert.Contains($"Branch {branch} already exists", unowned.Reason, StringComparison.Ordinal);
        Assert.Contains("names no parcel as its owner", unowned.Reason, StringComparison.Ordinal);
        Assert.Contains($"Branch {branch} already exists", owned.Reason, StringComparison.Ordinal);
        Assert.Contains($"belongs to parcel {other}", owned.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("worktree"), owned.Lever);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("worktree add", StringComparison.Ordinal) && c.EndsWith(branch, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_owner_setting_left_with_no_branch_takes_the_first_run_path_and_is_overwritten()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), Ids.New().ToString());

        Done(await DispatchAsync(parcelId, Ids.New()));

        Assert.Contains(runner.GitCalls, c => c.StartsWith("fetch ", StringComparison.Ordinal));
        Assert.Equal(parcelId.ToString(), runner.Config(Clone, ProjectClones.OwnerKeyOf(branch)));
    }

    [Fact]
    public async Task A_failing_git_command_throws_naming_the_command_and_quoting_only_the_last_twenty_lines_of_its_error_output()
    {
        string error = string.Join('\n', Enumerable.Range(1, 30).Select(n => $"remote line {n}")) + "\n";
        runner.On(c => c.IsGit("fetch"), ScriptedCommandRunner.Fails(128, error));

        GitCommandException failed = await Assert.ThrowsAsync<GitCommandException>(() => DispatchAsync(Ids.New(), Ids.New()));

        Assert.StartsWith("git --git-dir=", failed.Message, StringComparison.Ordinal);
        Assert.Contains(" fetch ", failed.Message, StringComparison.Ordinal);
        Assert.Contains("exited with code 128", failed.Message, StringComparison.Ordinal);
        Assert.Contains("remote line 11", failed.Message, StringComparison.Ordinal);
        Assert.Contains("remote line 30", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("remote line 10\n", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("branch ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_git_command_runs_with_the_limit_for_one_git_command()
    {
        Done(await DispatchAsync(Ids.New(), Ids.New()));

        Assert.NotEmpty(runner.Commands);
        Assert.All(runner.Commands, c => Assert.True(c.Program != "git" || c.Limit == Git.Limit));
    }

    [Fact]
    public async Task A_fetch_ended_at_its_limit_throws_saying_it_was_ended_rather_than_naming_an_exit_code()
    {
        runner.On(c => c.IsGit("fetch"), CommandResult.LimitReached(string.Empty, "remote: Counting objects\n", "remote: Counting objects\n"));

        GitCommandException failed = await Assert.ThrowsAsync<GitCommandException>(() => DispatchAsync(Ids.New(), Ids.New()));

        Assert.Contains(" fetch ", failed.Message, StringComparison.Ordinal);
        Assert.Contains("was still running after 30 minutes and was ended", failed.Message, StringComparison.Ordinal);
        Assert.Contains("Counting objects", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_git_command_whose_output_was_cut_short_throws_rather_than_being_read_in_part()
    {
        runner.On(c => c.IsGit("ls-tree"), ScriptedCommandRunner.Ok(string.Empty) with { Truncated = true });

        GitCommandException failed = await Assert.ThrowsAsync<GitCommandException>(() => DispatchAsync(Ids.New(), Ids.New()));

        Assert.Contains(" ls-tree ", failed.Message, StringComparison.Ordinal);
        Assert.Contains("wrote more output than the daemon keeps", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("worktree add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_the_core_shows_the_module_no_record_of_is_refused_quoting_the_core_and_runs_nothing()
    {
        CoreApiException refused = await Assert.ThrowsAsync<CoreApiException>(
            () => node.DispatchAsync(SoftwareSteps.Worktree, Ids.New(), Ids.New(), project: "nowhere"));

        Assert.Contains("project 'nowhere'", refused.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_temporary_clone_an_earlier_clone_left_part_way_is_deleted_before_the_next_clone_starts()
    {
        string partial = node.Home.Clones().ProjectClonePartialPath(Project);
        Directory.CreateDirectory(Path.Combine(partial, "objects"));
        await File.WriteAllTextAsync(Path.Combine(partial, "objects", "half"), "left by a clone ended part way", None);
        runner.On(c => c.IsGit("init"), (command, _) =>
        {
            Assert.False(Directory.Exists(partial), "the leftover is deleted before the clone starts");
            Directory.CreateDirectory(command.Arguments[^1]);
            return Task.FromResult(ScriptedCommandRunner.Ok());
        });

        Done(await DispatchAsync(Ids.New(), Ids.New()));

        Assert.False(File.Exists(Path.Combine(Clone, "objects", "half")));
        Assert.False(Directory.Exists(partial));
    }

    [Fact]
    public async Task A_directory_at_the_clones_path_that_git_does_not_recognize_pauses_naming_it_and_is_left_alone()
    {
        Directory.CreateDirectory(Clone);
        await File.WriteAllTextAsync(Path.Combine(Clone, "notes.txt"), "not a repository", None);
        runner.On(c => c.IsGit("rev-parse", "--is-bare-repository"), ScriptedCommandRunner.Fails(128, $"fatal: not a git repository: '{Clone}'"));

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(Ids.New(), await AtWorktreeAsync()));

        Assert.Contains(Clone, paused.Reason, StringComparison.Ordinal);
        Assert.Contains("does not recognize", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(["rev-parse --is-bare-repository"], runner.GitCalls);
        Assert.True(File.Exists(Path.Combine(Clone, "notes.txt")), "nothing at the clone's path is ever deleted");
    }

    [Fact]
    public async Task A_stale_worktree_entry_for_the_branch_is_removed_before_a_new_worktree_is_added()
    {
        Guid parcelId = Ids.New();
        Guid flowRunId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await DispatchAsync(Ids.New(), Ids.New());
        runner.AddBranch(Clone, branch, ScriptedCommandRunner.PrimaryHead);
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), parcelId.ToString());
        string workPath = node.Home.FlowRunWorkPath(Project, flowRunId);
        string earlier = node.Home.FlowRunWorkPath(Project, Ids.New());
        runner.AddWorktree(Clone, workPath, branch, ScriptedCommandRunner.PrimaryHead, "locked initializing");
        runner.AddWorktree(Clone, earlier, branch, ScriptedCommandRunner.PrimaryHead, "prunable gitdir file points to non-existent location");
        int before = runner.Commands.Count;

        Done(await DispatchAsync(parcelId, flowRunId));

        SequenceAssert.ContainsConsecutively(
            [
                $"worktree unlock {workPath}",
                "worktree prune",
                "worktree prune",
                $"worktree add --quiet {workPath} {branch}",
            ],
            runner.GitCalls.Skip(before));
        IReadOnlyList<string> actual = runner.Worktrees(Clone);
        // the stale entry is gone and the new one is listed
        Assert.Single(actual, p => p == workPath);
        Assert.DoesNotContain(earlier, actual);
    }

    [Fact]
    public async Task A_commit_that_tracks_files_under_coanda_pauses_naming_them_before_any_owner_branch_or_worktree_is_made()
    {
        Tracks(".coanda", ".coanda/task.md\0.coanda/notes/a.md\0");

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(Ids.New(), await AtWorktreeAsync()));

        Assert.Contains(".coanda/task.md, .coanda/notes/a.md", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(ScriptedCommandRunner.PrimaryHead, paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.GitCalls, c =>
            c.StartsWith("config coanda", StringComparison.Ordinal)
            || c.StartsWith("config branch.", StringComparison.Ordinal)
            || c.StartsWith("branch ", StringComparison.Ordinal)
            || c.StartsWith("worktree add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tracked_path_under_coanda_that_git_would_quote_by_default_still_pauses_the_step()
    {
        Tracks(".coanda", ".coanda/na\u00efve \"x\".md\0");

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(Ids.New(), await AtWorktreeAsync()));

        Assert.Contains(".coanda/na\u00efve \"x\".md", paused.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_commit_that_tracks_coanda_itself_as_a_file_link_or_submodule_pauses_before_any_worktree_is_made()
    {
        // ls-tree -r lists a .coanda that is no directory as the one entry .coanda.
        Tracks(".coanda", ".coanda\0");

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(Ids.New(), await AtWorktreeAsync()));

        Assert.Contains("tracks .coanda", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(ScriptedCommandRunner.PrimaryHead, paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("worktree add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_path_that_only_begins_with_coanda_does_not_count_as_tracked_under_it()
    {
        runner.On(c => c.IsGit("ls-tree", "-z"), ScriptedCommandRunner.Ok("src\0.coanda-notes\0.coanda.md\0"));

        Done(await DispatchAsync(Ids.New(), Ids.New()));
        // no root entry matched, so nothing is listed under one
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("ls-tree -r", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".Coanda")]
    [InlineData(".COANDA")]
    public async Task A_commit_that_tracks_coanda_in_another_letter_case_pauses_because_a_case_ignoring_filesystem_makes_it_the_same_directory(string name)
    {
        Tracks(name, $"{name}/task.md\0");

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(Ids.New(), await AtWorktreeAsync()));

        Assert.Contains($"{name}/task.md", paused.Reason, StringComparison.Ordinal);
        Assert.Contains($"ls-tree -r -z --name-only {ScriptedCommandRunner.PrimaryHead} -- {name}", runner.GitCalls);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("worktree add", StringComparison.Ordinal));
    }

    /// <summary>Scripts the commit's root listing to hold <paramref name="rootEntry"/>, and the listing under it.</summary>
    private void Tracks(string rootEntry, string under)
    {
        runner.On(c => c.IsGit("ls-tree", "-z"), ScriptedCommandRunner.Ok($"README.md\0{rootEntry}\0src\0"));
        runner.On(c => c.IsGit("ls-tree", "-r"), ScriptedCommandRunner.Ok(under));
    }

    [Fact]
    public async Task An_exclude_file_holding_the_line_twice_is_left_holding_it_once_and_its_other_lines_are_kept()
    {
        Directory.CreateDirectory(Path.Combine(Clone, "info"));
        await File.WriteAllTextAsync(ExcludePath, $"# kept\n{ProjectClones.ExcludeLine}\n*.log\n{ProjectClones.ExcludeLine}\n", None);

        Done(await DispatchAsync(Ids.New(), Ids.New()));

        Assert.Equal(["# kept", "*.log", ProjectClones.ExcludeLine], ExcludeLines());
    }

    [Fact]
    public async Task Removing_the_worktree_of_a_flow_run_in_a_project_with_no_plain_name_finds_none_and_runs_nothing()
    {
        await node.DeliverEndAsync(Ids.New(), "team/alpha");

        Assert.Empty(runner.Commands);
    }

    private const string Moved = "fedcba9876543210fedcba9876543210fedcba98";
    private const string Ahead = "aaaabbbbccccddddeeeeffff0000111122223333";

    private Task EndAsync(Guid flowRunId) => node.DeliverEndAsync(flowRunId);

    private async Task<Guid> TaskAsync()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Check me out", null, None);
        return parcel.Id;
    }

    /// <summary>Records, on a flow run of its own, an open-pr result for the parcel whose head is <paramref name="head"/>.</summary>
    private async Task RecordedPushAsync(Guid parcelId, string head)
    {
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, Recorder.Name, None);
        JsonElement result = JsonSerializer.SerializeToElement(new { verdict = "opened", systemStep = SoftwareSteps.OpenPr, number = 17, head });
        await flowRuns.RecordResultAsync(Principal.LocalOwner, Project, started.Id, "open-pr", SoftwareSteps.OpenPr, result, None);
        await flowRuns.StopAsync(Principal.LocalOwner, Project, started.Id, "only its record is wanted", None);
    }

    /// <summary>A task whose branch a live flow run of its own has checked out at that flow run's work path.</summary>
    private async Task<(Guid ParcelId, string Branch, Guid Holder, string Held)> HeldByFlowRunAsync()
    {
        Guid parcelId = await TaskAsync();
        FlowRun holder = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, Recorder.Name, None);
        Done(await DispatchAsync(parcelId, holder.Id));
        return (parcelId, $"coanda/{Ids.Short(parcelId)}", holder.Id, node.Home.FlowRunWorkPath(Project, holder.Id));
    }

    private string ExcludePath => Path.Combine(Clone, "info", "exclude");

    private string[] ExcludeLines() => File.ReadAllText(ExcludePath).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    private Task<SystemStepAnswer> DispatchAsync(Guid parcelId, Guid flowRunId) =>
        node.DispatchAsync(SoftwareSteps.Worktree, parcelId, flowRunId);

    /// <summary>A flow run, on a parcel of its own, sitting in its worktree step, which a pause names.</summary>
    private async Task<Guid> AtWorktreeAsync() =>
        (await node.HeldAsync(await TaskAsync(), "worktree")).FlowRunId;

    private static JsonElement Done(SystemStepAnswer outcome)
    {
        JsonElement result = Assert.IsType<SystemStepAnswer.Done>(outcome).Result;
        Assert.Equal("done", result.GetProperty("verdict").GetString());
        Assert.Equal(SoftwareSteps.Worktree, result.GetProperty("systemStep").GetString());
        return result;
    }

    private static SystemStepAnswer.Paused Paused(SystemStepAnswer outcome) => Assert.IsType<SystemStepAnswer.Paused>(outcome);
}
