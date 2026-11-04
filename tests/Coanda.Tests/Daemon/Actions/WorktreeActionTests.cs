using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Commands;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.Flows;
using Fisher;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.Actions;

public sealed class WorktreeActionTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal FlowPrincipal = new("flow:under-test", Role.Agent);
    private static readonly ContourStep Step = new() { Name = "worktree", Action = SoftwareActions.Worktree };

    /// <summary>A contour whose one step is open-pr, so a flow of it can hold a recorded push.</summary>
    private static readonly Contour Recorder = new()
    {
        Name = "recorder",
        Accepts = ["task"],
        Outcome = "recorded",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            new ContourStep
            {
                Name = "open-pr",
                Action = SoftwareActions.OpenPr,
                Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("opened"), VisitCap = 1, Target = Contour.End }],
            },
        ],
    };

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private readonly FlowWorkers workers = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private FlowService flows = null!;
    private WorktreeAction action = null!;

    private string Clone => temp.Home.ProjectClonePath(Project);

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        parcels = new ParcelService(store, TimeProvider.System);
        flows = new FlowService(store, TimeProvider.System);
        var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Contour, DefinitionScope.Project, Project, Recorder.Name,
            Contour.CanonicalJson(Recorder), Recorder.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        action = new WorktreeAction(
            new ProjectService(store, TimeProvider.System),
            new ProjectClones(temp.Home, runner, NullLogger<ProjectClones>.Instance),
            flows,
            workers);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task A_first_run_clones_the_project_cuts_the_parcels_branch_from_the_fetched_primary_and_adds_the_worktree()
    {
        Guid parcelId = Ids.New();
        Guid flowId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        string workPath = temp.Home.FlowWorkPath(Project, flowId);
        string partial = temp.Home.ProjectClonePartialPath(Project);

        JsonElement result = Done(await PerformAsync(parcelId, flowId));

        result.GetProperty("branch").GetString().Should().Be(branch);
        result.GetProperty("head").GetString().Should().Be(ScriptedCommandRunner.PrimaryHead);
        runner.GitCalls.Should().Equal(
            $"init --bare --quiet {partial}",
            $"remote add origin {RegisteredProjects.Remote}",
            "worktree list --porcelain",
            $"for-each-ref --format=%(objectname) refs/heads/{branch}",
            $"fetch --no-tags --quiet origin +refs/heads/{branch}:{ProjectClones.TrackingRefOf(branch)}",
            $"update-ref -d {ProjectClones.TrackingRefOf(branch)}",
            $"fetch --no-tags --quiet origin +refs/heads/{RegisteredProjects.Branch}:refs/remotes/origin/{RegisteredProjects.Branch}",
            $"rev-parse --verify --quiet refs/remotes/origin/{RegisteredProjects.Branch}^{{commit}}",
            $"ls-tree -z --name-only {ScriptedCommandRunner.PrimaryHead}",
            $"config {ProjectClones.OwnerKeyOf(branch)} {parcelId}",
            $"branch --no-track {branch} {ScriptedCommandRunner.PrimaryHead}",
            $"worktree add --quiet {workPath} {branch}");
        runner.Commands.Skip(1).Should().OnlyContain(c => c.GitDirectory() == Clone || c.GitDirectory() == partial);
        runner.Config(Clone, ProjectClones.OwnerKeyOf(branch)).Should().Be(parcelId.ToString());
        Directory.Exists(Clone).Should().BeTrue("the clone is moved into place once it is made");
        Directory.Exists(partial).Should().BeFalse();
        ExcludeLines().Should().ContainSingle(l => l == ProjectClones.ExcludeLine);
    }

    [Fact]
    public async Task Every_git_command_runs_with_terminal_prompts_off_ssh_in_batch_mode_and_the_C_locale()
    {
        await PerformAsync(Ids.New(), Ids.New());

        runner.Commands.Should().NotBeEmpty().And.OnlyContain(c =>
            c.Program == "git"
            && c.Environment["GIT_TERMINAL_PROMPT"] == "0"
            && c.Environment["GIT_SSH_COMMAND"].Contains("BatchMode=yes")
            && c.Environment["LC_ALL"] == "C");
    }

    [Fact]
    public async Task A_repeat_run_on_a_work_path_that_holds_the_worktree_answers_its_head_and_fetches_creates_and_checks_out_nothing()
    {
        Guid parcelId = Ids.New();
        Guid flowId = Ids.New();
        await PerformAsync(parcelId, flowId);
        DateTime written = File.GetLastWriteTimeUtc(ExcludePath);
        int before = runner.Commands.Count;

        JsonElement again = Done(await PerformAsync(parcelId, flowId));

        again.GetProperty("branch").GetString().Should().Be($"coanda/{Ids.Short(parcelId)}");
        again.GetProperty("head").GetString().Should().Be(ScriptedCommandRunner.PrimaryHead);
        runner.GitCalls.Skip(before).Should().Equal("rev-parse --is-bare-repository", "worktree list --porcelain");
        ExcludeLines().Should().ContainSingle(l => l == ProjectClones.ExcludeLine);
        File.GetLastWriteTimeUtc(ExcludePath).Should().Be(written, "the exclude line is written only when it is missing");
    }

    [Fact]
    public async Task A_branch_an_earlier_flow_on_the_parcel_created_is_checked_out_at_its_own_tip_when_the_remote_has_no_branch_of_that_name()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await PerformAsync(parcelId, earlier);
        await EndAsync(earlier);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), Moved);
        int before = runner.Commands.Count;
        Guid later = Ids.New();

        JsonElement result = Done(await PerformAsync(parcelId, later));

        result.GetProperty("head").GetString().Should().Be(Moved, "the branch is checked out at its own tip, not the primary's");
        runner.GitCalls.Skip(before).Should().Equal(
            "rev-parse --is-bare-repository",
            "worktree list --porcelain",
            $"for-each-ref --format=%(objectname) refs/heads/{branch}",
            $"config --get --default  {ProjectClones.OwnerKeyOf(branch)}",
            $"fetch --no-tags --quiet origin +refs/heads/{branch}:{ProjectClones.TrackingRefOf(branch)}",
            $"update-ref -d {ProjectClones.TrackingRefOf(branch)}",
            $"ls-tree -z --name-only {Moved}",
            $"worktree add --quiet {temp.Home.FlowWorkPath(Project, later)} {branch}");
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().BeNull(
            "someone deleted the branch on the remote, so the next push's lease expects it absent");
    }

    [Fact]
    public async Task A_branch_the_remote_has_moved_ahead_of_moves_forward_to_the_remotes_commit_before_the_worktree_is_added()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await PerformAsync(parcelId, earlier);
        await EndAsync(earlier);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);
        runner.AddAncestry(Moved, Ahead);
        Guid later = Ids.New();

        JsonElement result = Done(await PerformAsync(parcelId, later));

        result.GetProperty("head").GetString().Should().Be(Ahead, "a commit someone added on the remote is built on");
        runner.Branch(Clone, branch).Should().Be(Ahead);
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Ahead, "the next push's lease expects what the remote holds now");
        runner.GitCalls.Should().ContainInOrder(
            $"update-ref refs/heads/{branch} {Ahead} {Moved}",
            $"ls-tree -z --name-only {Ahead}",
            $"worktree add --quiet {temp.Home.FlowWorkPath(Project, later)} {branch}");
    }

    [Fact]
    public async Task A_branch_ahead_of_the_remotes_stays_at_its_own_tip_and_the_lease_expects_the_remotes()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await PerformAsync(parcelId, earlier);
        await EndAsync(earlier);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, ScriptedCommandRunner.PrimaryHead);
        runner.AddAncestry(ScriptedCommandRunner.PrimaryHead, Moved);

        JsonElement result = Done(await PerformAsync(parcelId, Ids.New()));

        result.GetProperty("head").GetString().Should().Be(Moved);
        runner.Branch(Clone, branch).Should().Be(Moved);
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(ScriptedCommandRunner.PrimaryHead);
        runner.GitCalls.Should().NotContain(c => c.StartsWith($"update-ref refs/heads/{branch}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_that_has_diverged_from_the_remotes_pauses_naming_how_to_reconcile_them_and_is_neither_moved_nor_checked_out()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await PerformAsync(parcelId, earlier);
        await EndAsync(earlier);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);
        int before = runner.Commands.Count;

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcelId, Ids.New()));

        paused.Reason.Should().Contain($"Branch {branch}").And.Contain(Moved).And.Contain(Ahead).And.Contain("Reconcile them by hand").And.Contain(Clone);
        paused.Lever.Should().Be(FlowLevers.PerformAgain("worktree"));
        runner.Branch(Clone, branch).Should().Be(Moved);
        runner.GitCalls.Skip(before).Should().NotContain(c => c.StartsWith("worktree add", StringComparison.Ordinal) || c.StartsWith("update-ref", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_the_clone_does_not_hold_but_the_remote_does_is_cut_from_the_remotes_branch_rather_than_the_primary()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await PerformAsync(Ids.New(), Ids.New());
        runner.SetRemoteBranch(Clone, branch, Moved);
        int before = runner.Commands.Count;

        JsonElement result = Done(await PerformAsync(parcelId, Ids.New()));

        result.GetProperty("head").GetString().Should().Be(Moved);
        runner.Branch(Clone, branch).Should().Be(Moved);
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Moved);
        runner.GitCalls.Skip(before).Should().Contain($"branch --no-track {branch} {Moved}")
            .And.NotContain(c => c.StartsWith($"fetch --no-tags --quiet origin +refs/heads/{RegisteredProjects.Branch}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_a_live_flows_worktree_has_checked_out_pauses_naming_that_flow_and_is_neither_fetched_nor_moved()
    {
        (Guid parcelId, string branch, Guid holder, string held) = await HeldByFlowAsync();
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);
        runner.AddAncestry(Moved, Ahead);
        int before = runner.Commands.Count;

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcelId, Ids.New()));

        paused.Reason.Should().Contain($"flow {holder}").And.Contain("still live").And.Contain(held);
        paused.Lever.Should().Be(FlowLevers.PerformAgain("worktree"));
        runner.GitCalls.Skip(before).Should().NotContain(c =>
            c.StartsWith("fetch ", StringComparison.Ordinal) || c.StartsWith("update-ref", StringComparison.Ordinal) || c.StartsWith("worktree remove", StringComparison.Ordinal) || c.StartsWith("worktree add", StringComparison.Ordinal));
        runner.Branch(Clone, branch).Should().Be(Moved);
    }

    [Fact]
    public async Task A_branch_held_by_a_worktree_at_a_path_that_is_no_flows_work_path_pauses_naming_that_path()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await PerformAsync(Ids.New(), Ids.New());
        runner.AddBranch(Clone, branch, Moved);
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), parcelId.ToString());
        string mine = Path.Combine(temp.Home.ClonesPath, "someones-own-worktree");
        runner.AddWorktree(Clone, mine, branch, Moved);

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcelId, Ids.New()));

        paused.Reason.Should().Contain(mine).And.Contain("no work path of a flow the node holds");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("worktree"));
        runner.Worktrees(Clone).Should().Contain(mine, "nothing removes a worktree whose flow is unknown");
    }

    [Fact]
    public async Task A_branch_held_by_the_worktree_of_a_flow_the_store_does_not_hold_pauses_naming_that_path()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await PerformAsync(Ids.New(), Ids.New());
        runner.AddBranch(Clone, branch, Moved);
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), parcelId.ToString());
        string unknown = temp.Home.FlowWorkPath(Project, Ids.New());
        runner.AddWorktree(Clone, unknown, branch, Moved);

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcelId, Ids.New()));

        paused.Reason.Should().Contain(unknown);
        paused.Lever.Should().Be(FlowLevers.PerformAgain("worktree"));
        runner.Worktrees(Clone).Should().Contain(unknown);
    }

    [Fact]
    public async Task A_branch_held_by_a_stopped_flows_worktree_is_taken_over_removing_that_worktree_and_checking_the_branch_out_here()
    {
        (Guid parcelId, string branch, Guid holder, string held) = await HeldByFlowAsync();
        await flows.StopAsync(Principal.LocalOwner, Project, holder, "the build is done with it", None);
        Guid taker = Ids.New();
        string workPath = temp.Home.FlowWorkPath(Project, taker);
        int before = runner.Commands.Count;

        JsonElement result = Done(await PerformAsync(parcelId, taker));

        result.GetProperty("branch").GetString().Should().Be(branch);
        runner.GitCalls.Skip(before).Should().ContainInOrder(
            $"worktree remove --force --force {held}",
            "worktree list --porcelain",
            $"fetch --no-tags --quiet origin +refs/heads/{branch}:{ProjectClones.TrackingRefOf(branch)}",
            $"worktree add --quiet {workPath} {branch}");
        runner.Worktrees(Clone).Should().Equal(workPath);

        // A removal of the stopped flow's worktree that runs afterwards finds nothing at its work path.
        before = runner.Commands.Count;
        (await Clones().RemoveAsync(Project, holder, None)).Should().BeFalse();
        runner.GitCalls.Skip(before).Should().NotContain(c => c.StartsWith("worktree remove", StringComparison.Ordinal) || c.StartsWith("worktree prune", StringComparison.Ordinal));
        runner.Worktrees(Clone).Should().Equal(workPath);
    }

    [Fact]
    public async Task A_takeover_whose_holding_worktree_was_already_removed_answers_done()
    {
        (Guid parcelId, string branch, Guid holder, _) = await HeldByFlowAsync();
        await flows.StopAsync(Principal.LocalOwner, Project, holder, "the build is done with it", None);
        (await Clones().RemoveAsync(Project, holder, None)).Should().BeTrue("the stopped flow's own removal runs first");
        Guid taker = Ids.New();

        JsonElement result = Done(await PerformAsync(parcelId, taker));

        result.GetProperty("branch").GetString().Should().Be(branch);
        runner.Worktrees(Clone).Should().Equal(temp.Home.FlowWorkPath(Project, taker));
    }

    [Fact]
    public async Task A_takeover_from_a_stopped_flow_whose_worker_is_still_performing_removes_and_adds_nothing_until_that_worker_returns()
    {
        (Guid parcelId, string branch, Guid holder, string held) = await HeldByFlowAsync();
        await flows.StopAsync(Principal.LocalOwner, Project, holder, "the build is done with it", None);
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workers.Started(holder, working.Task);
        Guid taker = Ids.New();
        int before = runner.Commands.Count;

        Task<ActionOutcome> takeover = PerformAsync(parcelId, taker).AsTask();
        await Flowing.UntilAsync(() => runner.GitCalls.Skip(before).Any(c => c == "worktree list --porcelain"), "the takeover has listed the worktrees");
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        takeover.IsCompleted.Should().BeFalse("the takeover waits for the stopped flow's worker");
        runner.GitCalls.Skip(before).Should().NotContain(c => c.StartsWith("worktree remove", StringComparison.Ordinal) || c.StartsWith("worktree add", StringComparison.Ordinal));
        (await Clones().RemoveAsync(Project, Ids.New(), None)).Should().BeFalse("the clone's turn is not held while the takeover waits");

        workers.Returned(holder);
        working.SetResult();
        Done(await takeover.WaitAsync(TimeSpan.FromSeconds(15)));
        runner.GitCalls.Skip(before).Should().Contain($"worktree remove --force --force {held}").And.Contain($"worktree add --quiet {temp.Home.FlowWorkPath(Project, taker)} {branch}");
    }

    [Fact]
    public async Task A_takeover_waiting_on_a_worker_ends_when_its_own_step_is_cancelled()
    {
        (Guid parcelId, _, Guid holder, _) = await HeldByFlowAsync();
        await flows.StopAsync(Principal.LocalOwner, Project, holder, "the build is done with it", None);
        workers.Started(holder, new TaskCompletionSource().Task);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Func<Task> takeover = async () => await action.PerformAsync(
            new ActionRequest(FlowPrincipal, Project, Ids.New(), parcelId, ShippedContours.Build, Step, Resumed: false), stop.Token);

        await takeover.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_branch_rewritten_in_the_clone_while_the_remote_still_holds_the_last_recorded_push_is_checked_out_as_it_stands()
    {
        Guid parcelId = await TaskAsync();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await PerformAsync(parcelId, earlier);
        await EndAsync(earlier);
        await RecordedPushAsync(parcelId, Ahead);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);
        int before = runner.Commands.Count;
        Guid later = Ids.New();

        JsonElement result = Done(await PerformAsync(parcelId, later));

        result.GetProperty("head").GetString().Should().Be(Moved, "the clone's branch is checked out as it stands");
        runner.Branch(Clone, branch).Should().Be(Moved);
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Ahead, "the push's lease expects what the remote holds");
        runner.GitCalls.Skip(before).Should().Contain($"worktree add --quiet {temp.Home.FlowWorkPath(Project, later)} {branch}")
            .And.NotContain(c => c.StartsWith($"update-ref refs/heads/{branch}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_diverged_from_a_remote_holding_a_commit_nothing_recorded_is_still_refused()
    {
        Guid parcelId = await TaskAsync();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        Guid earlier = Ids.New();
        await PerformAsync(parcelId, earlier);
        await EndAsync(earlier);
        await RecordedPushAsync(parcelId, ScriptedCommandRunner.PrimaryHead);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Ahead);

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcelId, Ids.New()));

        paused.Reason.Should().Contain("Reconcile them by hand").And.Contain("not the commit last recorded as pushed");
        runner.Branch(Clone, branch).Should().Be(Moved);
    }

    [Fact]
    public async Task An_existing_branch_another_parcel_owns_or_no_parcel_owns_pauses_naming_the_branch_and_is_not_checked_out()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await PerformAsync(Ids.New(), Ids.New());
        runner.AddBranch(Clone, branch, ScriptedCommandRunner.PrimaryHead);

        ActionOutcome.Paused unowned = Paused(await PerformAsync(parcelId, Ids.New()));
        Guid other = Ids.New();
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), other.ToString());
        ActionOutcome.Paused owned = Paused(await PerformAsync(parcelId, Ids.New()));

        unowned.Reason.Should().Contain($"Branch {branch} already exists").And.Contain("names no parcel as its owner");
        owned.Reason.Should().Contain($"Branch {branch} already exists").And.Contain($"belongs to parcel {other}");
        owned.Lever.Should().Be(FlowLevers.PerformAgain("worktree"));
        runner.GitCalls.Should().NotContain(c => c.StartsWith("worktree add", StringComparison.Ordinal) && c.EndsWith(branch, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_owner_setting_left_with_no_branch_takes_the_first_run_path_and_is_overwritten()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), Ids.New().ToString());

        Done(await PerformAsync(parcelId, Ids.New()));

        runner.GitCalls.Should().Contain(c => c.StartsWith("fetch ", StringComparison.Ordinal));
        runner.Config(Clone, ProjectClones.OwnerKeyOf(branch)).Should().Be(parcelId.ToString());
    }

    [Fact]
    public async Task A_failing_git_command_throws_naming_the_command_and_quoting_only_the_last_twenty_lines_of_its_error_output()
    {
        string error = string.Join('\n', Enumerable.Range(1, 30).Select(n => $"remote line {n}")) + "\n";
        runner.On(c => c.IsGit("fetch"), ScriptedCommandRunner.Fails(128, error));

        GitCommandException failed = (await FluentActions.Awaiting(() => PerformAsync(Ids.New(), Ids.New()).AsTask())
            .Should().ThrowAsync<GitCommandException>()).Subject.Single();

        failed.Message.Should().StartWith("git --git-dir=").And.Contain(" fetch ").And.Contain("exited with code 128");
        failed.Message.Should().Contain("remote line 11").And.Contain("remote line 30").And.NotContain("remote line 10\n");
        runner.GitCalls.Should().NotContain(c => c.StartsWith("branch ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_git_command_runs_with_the_limit_for_one_git_command()
    {
        Done(await PerformAsync(Ids.New(), Ids.New()));

        runner.Commands.Should().NotBeEmpty().And.OnlyContain(c => c.Program != "git" || c.Limit == Git.Limit);
    }

    [Fact]
    public async Task A_fetch_ended_at_its_limit_throws_saying_it_was_ended_rather_than_naming_an_exit_code()
    {
        runner.On(c => c.IsGit("fetch"), CommandResult.LimitReached(string.Empty, "remote: Counting objects\n", "remote: Counting objects\n"));

        GitCommandException failed = (await FluentActions.Awaiting(() => PerformAsync(Ids.New(), Ids.New()).AsTask())
            .Should().ThrowAsync<GitCommandException>()).Subject.Single();

        failed.Message.Should().Contain(" fetch ").And.Contain("was still running after 30 minutes and was ended").And.Contain("Counting objects");
    }

    [Fact]
    public async Task A_git_command_whose_output_was_cut_short_throws_rather_than_being_read_in_part()
    {
        runner.On(c => c.IsGit("ls-tree"), ScriptedCommandRunner.Ok(string.Empty) with { Truncated = true });

        GitCommandException failed = (await FluentActions.Awaiting(() => PerformAsync(Ids.New(), Ids.New()).AsTask())
            .Should().ThrowAsync<GitCommandException>()).Subject.Single();

        failed.Message.Should().Contain(" ls-tree ").And.Contain("wrote more output than the daemon keeps");
        runner.GitCalls.Should().NotContain(c => c.StartsWith("worktree add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_with_no_record_pauses_naming_the_step_and_the_project_and_runs_nothing()
    {
        ActionOutcome outcome = await action.PerformAsync(
            new ActionRequest(FlowPrincipal, "nowhere", Ids.New(), Ids.New(), ShippedContours.Build, Step, Resumed: false), None);

        ActionOutcome.Paused paused = Paused(outcome);
        paused.Reason.Should().StartWith("Step 'worktree'").And.Contain("Project 'nowhere' is not registered");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("worktree"));
        runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_temporary_clone_an_earlier_clone_left_part_way_is_deleted_before_the_next_clone_starts()
    {
        string partial = temp.Home.ProjectClonePartialPath(Project);
        Directory.CreateDirectory(Path.Combine(partial, "objects"));
        await File.WriteAllTextAsync(Path.Combine(partial, "objects", "half"), "left by a clone ended part way", None);
        runner.On(c => c.IsGit("init"), (command, _) =>
        {
            Directory.Exists(partial).Should().BeFalse("the leftover is deleted before the clone starts");
            Directory.CreateDirectory(command.Arguments[^1]);
            return Task.FromResult(ScriptedCommandRunner.Ok());
        });

        Done(await PerformAsync(Ids.New(), Ids.New()));

        File.Exists(Path.Combine(Clone, "objects", "half")).Should().BeFalse();
        Directory.Exists(partial).Should().BeFalse();
    }

    [Fact]
    public async Task A_directory_at_the_clones_path_that_git_does_not_recognize_pauses_naming_it_and_is_left_alone()
    {
        Directory.CreateDirectory(Clone);
        await File.WriteAllTextAsync(Path.Combine(Clone, "notes.txt"), "not a repository", None);
        runner.On(c => c.IsGit("rev-parse", "--is-bare-repository"), ScriptedCommandRunner.Fails(128, $"fatal: not a git repository: '{Clone}'"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(Ids.New(), Ids.New()));

        paused.Reason.Should().Contain(Clone).And.Contain("does not recognize");
        runner.GitCalls.Should().Equal("rev-parse --is-bare-repository");
        File.Exists(Path.Combine(Clone, "notes.txt")).Should().BeTrue("nothing at the clone's path is ever deleted");
    }

    [Fact]
    public async Task A_stale_worktree_entry_for_the_branch_is_removed_before_a_new_worktree_is_added()
    {
        Guid parcelId = Ids.New();
        Guid flowId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await PerformAsync(Ids.New(), Ids.New());
        runner.AddBranch(Clone, branch, ScriptedCommandRunner.PrimaryHead);
        runner.SetConfig(Clone, ProjectClones.OwnerKeyOf(branch), parcelId.ToString());
        string workPath = temp.Home.FlowWorkPath(Project, flowId);
        string earlier = temp.Home.FlowWorkPath(Project, Ids.New());
        runner.AddWorktree(Clone, workPath, branch, ScriptedCommandRunner.PrimaryHead, "locked initializing");
        runner.AddWorktree(Clone, earlier, branch, ScriptedCommandRunner.PrimaryHead, "prunable gitdir file points to non-existent location");
        int before = runner.Commands.Count;

        Done(await PerformAsync(parcelId, flowId));

        runner.GitCalls.Skip(before).Should().ContainInConsecutiveOrder(
            $"worktree unlock {workPath}",
            "worktree prune",
            "worktree prune",
            $"worktree add --quiet {workPath} {branch}");
        runner.Worktrees(Clone).Should().ContainSingle(p => p == workPath, "the stale entry is gone and the new one is listed")
            .And.NotContain(earlier);
    }

    [Fact]
    public async Task A_commit_that_tracks_files_under_coanda_pauses_naming_them_before_any_owner_branch_or_worktree_is_made()
    {
        Tracks(".coanda", ".coanda/task.md\0.coanda/notes/a.md\0");

        ActionOutcome.Paused paused = Paused(await PerformAsync(Ids.New(), Ids.New()));

        paused.Reason.Should().Contain(".coanda/task.md, .coanda/notes/a.md").And.Contain(ScriptedCommandRunner.PrimaryHead);
        runner.GitCalls.Should().NotContain(c =>
            c.StartsWith("config coanda", StringComparison.Ordinal)
            || c.StartsWith("config branch.", StringComparison.Ordinal)
            || c.StartsWith("branch ", StringComparison.Ordinal)
            || c.StartsWith("worktree add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tracked_path_under_coanda_that_git_would_quote_by_default_still_pauses_the_step()
    {
        Tracks(".coanda", ".coanda/na\u00efve \"x\".md\0");

        ActionOutcome.Paused paused = Paused(await PerformAsync(Ids.New(), Ids.New()));

        paused.Reason.Should().Contain(".coanda/na\u00efve \"x\".md");
    }

    [Fact]
    public async Task A_commit_that_tracks_coanda_itself_as_a_file_link_or_submodule_pauses_before_any_worktree_is_made()
    {
        // ls-tree -r lists a .coanda that is no directory as the one entry .coanda.
        Tracks(".coanda", ".coanda\0");

        ActionOutcome.Paused paused = Paused(await PerformAsync(Ids.New(), Ids.New()));

        paused.Reason.Should().Contain("tracks .coanda").And.Contain(ScriptedCommandRunner.PrimaryHead);
        runner.GitCalls.Should().NotContain(c => c.StartsWith("worktree add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_path_that_only_begins_with_coanda_does_not_count_as_tracked_under_it()
    {
        runner.On(c => c.IsGit("ls-tree", "-z"), ScriptedCommandRunner.Ok("src\0.coanda-notes\0.coanda.md\0"));

        Done(await PerformAsync(Ids.New(), Ids.New()));
        runner.GitCalls.Should().NotContain(c => c.StartsWith("ls-tree -r", StringComparison.Ordinal), "no root entry matched, so nothing is listed under one");
    }

    [Theory]
    [InlineData(".Coanda")]
    [InlineData(".COANDA")]
    public async Task A_commit_that_tracks_coanda_in_another_letter_case_pauses_because_a_case_ignoring_filesystem_makes_it_the_same_directory(string name)
    {
        Tracks(name, $"{name}/task.md\0");

        ActionOutcome.Paused paused = Paused(await PerformAsync(Ids.New(), Ids.New()));

        paused.Reason.Should().Contain($"{name}/task.md");
        runner.GitCalls.Should().Contain($"ls-tree -r -z --name-only {ScriptedCommandRunner.PrimaryHead} -- {name}");
        runner.GitCalls.Should().NotContain(c => c.StartsWith("worktree add", StringComparison.Ordinal));
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

        Done(await PerformAsync(Ids.New(), Ids.New()));

        ExcludeLines().Should().Equal("# kept", "*.log", ProjectClones.ExcludeLine);
    }

    [Fact]
    public async Task Removing_the_worktree_of_a_flow_in_a_project_with_no_plain_name_finds_none_and_runs_nothing()
    {
        var clones = new ProjectClones(temp.Home, runner, NullLogger<ProjectClones>.Instance);

        (await clones.RemoveAsync("team/alpha", Ids.New(), None)).Should().BeFalse();

        runner.Commands.Should().BeEmpty();
    }

    private const string Moved = "fedcba9876543210fedcba9876543210fedcba98";
    private const string Ahead = "aaaabbbbccccddddeeeeffff0000111122223333";

    private async Task EndAsync(Guid flowId) =>
        await Clones().RemoveAsync(Project, flowId, None);

    private ProjectClones Clones() => new(temp.Home, runner, NullLogger<ProjectClones>.Instance);

    private async Task<Guid> TaskAsync()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Check me out", null, None);
        return parcel.Id;
    }

    /// <summary>Records, on a flow of its own, an open-pr result for the parcel whose head is <paramref name="head"/>.</summary>
    private async Task RecordedPushAsync(Guid parcelId, string head)
    {
        Flow started = await flows.StartAsync(Principal.LocalOwner, Project, parcelId, Recorder.Name, None);
        JsonElement result = JsonSerializer.SerializeToElement(new { verdict = "opened", action = SoftwareActions.OpenPr, number = 17, head });
        await flows.RecordResultAsync(Principal.LocalOwner, Project, started.Id, "open-pr", SoftwareActions.OpenPr, result, None);
        await flows.StopAsync(Principal.LocalOwner, Project, started.Id, "only its record is wanted", None);
    }

    /// <summary>A task whose branch a live flow of its own has checked out at that flow's work path.</summary>
    private async Task<(Guid ParcelId, string Branch, Guid Holder, string Held)> HeldByFlowAsync()
    {
        Guid parcelId = await TaskAsync();
        Flow holder = await flows.StartAsync(Principal.LocalOwner, Project, parcelId, Recorder.Name, None);
        Done(await PerformAsync(parcelId, holder.Id));
        return (parcelId, $"coanda/{Ids.Short(parcelId)}", holder.Id, temp.Home.FlowWorkPath(Project, holder.Id));
    }

    private string ExcludePath => Path.Combine(Clone, "info", "exclude");

    private string[] ExcludeLines() => File.ReadAllText(ExcludePath).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    private ValueTask<ActionOutcome> PerformAsync(Guid parcelId, Guid flowId) =>
        action.PerformAsync(new ActionRequest(FlowPrincipal, Project, flowId, parcelId, ShippedContours.Build, Step, Resumed: false), None);

    private static JsonElement Done(ActionOutcome outcome)
    {
        JsonElement result = outcome.Should().BeOfType<ActionOutcome.Done>().Subject.Result;
        result.GetProperty("verdict").GetString().Should().Be("done");
        result.GetProperty("action").GetString().Should().Be(SoftwareActions.Worktree);
        return result;
    }

    private static ActionOutcome.Paused Paused(ActionOutcome outcome) =>
        outcome.Should().BeOfType<ActionOutcome.Paused>().Subject;
}
