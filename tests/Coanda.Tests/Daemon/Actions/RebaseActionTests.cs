using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;
using Coanda.Tests.Daemon.Commands;
using Fisher;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.Actions;

/// <summary>
/// The rebase action against the scripted command runner's model of the node's clone: the commands
/// it runs and their order, what it leaves the branch, the worktree, and the remote holding, and
/// each pause. The flow it runs on is a one-step open-pr contour whose result the test records by
/// hand, so the recorded head is that result's.
/// </summary>
public sealed class RebaseActionTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private const string Recorded = "aaaabbbbccccddddeeeeffff0000111122223333";
    private const string Moved = "fedcba9876543210fedcba9876543210fedcba98";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal FlowPrincipal = new("flow:under-test", Role.Agent);
    private static readonly ContourStep Step = new() { Name = "rebase", Action = SoftwareActions.Rebase };

    /// <summary>A contour whose one step is open-pr, so a flow of it can hold a recorded open-pr result.</summary>
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
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private FlowService flows = null!;
    private RebaseAction action = null!;

    private string Clone => temp.Home.ProjectClonePath(Project);

    private static string Rebased => ScriptedCommandRunner.RebasedOnto(Recorded, ScriptedCommandRunner.PrimaryHead);

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
        action = new RebaseAction(
            new ProjectService(store, TimeProvider.System),
            new ProjectClones(temp.Home, runner, NullLogger<ProjectClones>.Instance),
            flows);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task A_behind_branch_is_rebased_from_the_recorded_head_onto_the_fetched_primary_noted_pushed_under_its_lease_and_then_moved()
    {
        (Guid parcel, Guid flow, string branch, string workPath) = await ArrangeAsync();

        JsonElement result = Done(await PerformAsync(parcel, flow));

        result.GetProperty("head").GetString().Should().Be(Rebased);
        result.GetProperty("branch").GetString().Should().Be(branch);
        runner.GitCalls.Should().Equal(
            "rebase --abort",
            "status --porcelain --untracked-files=all",
            $"ls-remote --refs origin refs/heads/{branch}",
            $"for-each-ref --format=%(objectname) {ProjectClones.NoteRefOf(flow)}",
            $"for-each-ref --format=%(objectname) refs/heads/{branch}",
            $"fetch --no-tags --quiet origin +refs/heads/{RegisteredProjects.Branch}:refs/remotes/origin/{RegisteredProjects.Branch}",
            $"rev-parse --verify --quiet refs/remotes/origin/{RegisteredProjects.Branch}^{{commit}}",
            $"checkout --quiet --detach {Recorded}",
            $"-c rebase.updateRefs=false rebase --quiet {ScriptedCommandRunner.PrimaryHead}",
            "rev-parse --verify --quiet HEAD^{commit}",
            $"update-ref {ProjectClones.NoteRefOf(flow)} {Rebased}",
            $"push --force-with-lease=refs/heads/{branch}:{Recorded} origin {Rebased}:refs/heads/{branch}",
            $"checkout --quiet --detach {Rebased}",
            $"update-ref refs/heads/{branch} {Rebased} {Recorded}",
            $"checkout --quiet {branch}",
            $"update-ref {ProjectClones.TrackingRefOf(branch)} {Rebased}");
        runner.Commands.Where(c => c.GitDirectory() is null).Should().OnlyContain(c => c.WorkingDirectory == workPath, "the rebase works in the merge flow's own worktree");
        runner.Branch(Clone, branch).Should().Be(Rebased);
        runner.CheckedOut(Clone, workPath).Should().Be(branch);
        runner.RemoteBranch(Clone, branch).Should().Be(Rebased);
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Rebased);
        runner.TrackingRef(Clone, ProjectClones.NoteRefOf(flow)).Should().Be(Rebased);
    }

    [Fact]
    public async Task No_command_that_moves_the_branch_runs_before_the_push_and_the_note_is_written_before_it()
    {
        (Guid parcel, Guid flow, string branch, _) = await ArrangeAsync();

        Done(await PerformAsync(parcel, flow));

        IReadOnlyList<string> calls = runner.GitCalls;
        int push = IndexOf(calls, "push ");
        IndexOf(calls, $"update-ref {ProjectClones.NoteRefOf(flow)} ").Should().BeLessThan(push);
        IndexOf(calls, $"update-ref refs/heads/{branch} ").Should().BeGreaterThan(push);
        calls.Select((c, i) => (c, i)).Where(p => p.c == $"checkout --quiet {branch}").Should().OnlyContain(p => p.i > push);
    }

    [Fact]
    public async Task Every_command_runs_with_terminal_prompts_off_ssh_in_batch_mode_the_C_locale_and_the_git_limit()
    {
        (Guid parcel, Guid flow, _, _) = await ArrangeAsync();

        Done(await PerformAsync(parcel, flow));

        runner.Commands.Should().NotBeEmpty().And.OnlyContain(c =>
            c.Program == "git"
            && c.Environment["GIT_TERMINAL_PROMPT"] == "0"
            && c.Environment["GIT_SSH_COMMAND"].Contains("BatchMode=yes")
            && c.Environment["LC_ALL"] == "C"
            && c.Limit == Git.Limit);
    }

    [Fact]
    public async Task A_conflict_is_aborted_the_branch_checked_out_again_at_the_recorded_head_and_the_flow_paused_naming_the_unmerged_paths()
    {
        (Guid parcel, Guid flow, string branch, string workPath) = await ArrangeAsync();
        runner.ConflictOnRebase("src/Program.cs", "docs/a b.md");

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel, flow));

        paused.Reason.Should().StartWith("Step 'rebase' rebased nothing").And.Contain("stopped on a conflict")
            .And.Contain("src/Program.cs").And.Contain("docs/a b.md").And.Contain(Recorded);
        paused.Lever.Should().Be(FlowLevers.RebaseConflict(Clone, branch, RegisteredProjects.Branch));
        paused.Lever.Should().Contain("Stop the flow").And.Contain(Clone).And.Contain("outside the daemon's work paths")
            .And.Contain($"rebase {branch}").And.Contain("without pushing").And.Contain("Remove that worktree").And.Contain("move the task back to build")
            .And.NotContain("merge commit").And.NotContain("Update branch");
        runner.GitCalls.Should().ContainInOrder("rebase --abort", "-c rebase.updateRefs=false rebase --quiet " + ScriptedCommandRunner.PrimaryHead, "rebase --abort", $"checkout --quiet {branch}");
        runner.GitCalls.Should().NotContain(c => c.StartsWith("push", StringComparison.Ordinal) || c.StartsWith("update-ref", StringComparison.Ordinal));
        runner.Branch(Clone, branch).Should().Be(Recorded);
        runner.CheckedOut(Clone, workPath).Should().Be(branch);
        runner.RemoteBranch(Clone, branch).Should().Be(Recorded);
    }

    [Fact]
    public async Task A_push_the_lease_refuses_pauses_with_the_stop_lever_saying_someone_else_updated_the_branch()
    {
        (Guid parcel, Guid flow, string branch, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(1, $" ! [rejected]        {Rebased} -> {branch} (stale info)\nerror: failed to push some refs\n"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel, flow));

        paused.Reason.Should().Contain("someone else updated branch").And.Contain("(stale info)");
        paused.Lever.Should().Be(FlowLevers.RebaseRefused("rebase"));
        runner.Branch(Clone, branch).Should().Be(Recorded, "the branch moves only once the push has succeeded");
    }

    [Fact]
    public async Task A_worktree_with_changes_pauses_with_the_stop_lever_before_any_fetch_rebase_or_push()
    {
        (Guid parcel, Guid flow, _, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("status"), ScriptedCommandRunner.Ok(" M src/Program.cs\n?? notes.txt\n"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel, flow));

        paused.Reason.Should().Contain("has changes").And.Contain("src/Program.cs").And.Contain("notes.txt");
        paused.Lever.Should().Be(FlowLevers.RebaseRefused("rebase"));
        NothingFetchedRebasedOrPushed();
    }

    [Fact]
    public async Task A_branch_tip_that_is_not_the_recorded_head_pauses_with_the_stop_lever_before_any_fetch_rebase_or_push()
    {
        (Guid parcel, Guid flow, string branch, _) = await ArrangeAsync();
        runner.AddBranch(Clone, branch, Moved);

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel, flow));

        paused.Reason.Should().Contain($"is at {Moved}, not {Recorded}");
        paused.Lever.Should().Be(FlowLevers.RebaseRefused("rebase"));
        NothingFetchedRebasedOrPushed();
    }

    [Fact]
    public async Task A_remote_branch_and_tip_holding_a_commit_the_step_did_not_note_pause_with_the_stop_lever_and_record_no_head()
    {
        (Guid parcel, Guid flow, string branch, _) = await ArrangeAsync();
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Moved);

        ActionOutcome outcome = await PerformAsync(parcel, flow);

        ActionOutcome.Paused paused = Paused(outcome);
        paused.Lever.Should().Be(FlowLevers.RebaseRefused("rebase"));
        paused.Reason.Should().Contain($"the remote's branch, at {Moved}, is no commit this step noted and pushed");
        NothingFetchedRebasedOrPushed();
        runner.Branch(Clone, branch).Should().Be(Moved);
    }

    [Fact]
    public async Task A_push_that_fails_another_way_pauses_to_perform_again_quoting_twenty_lines_and_is_then_performed_again_from_the_recorded_head()
    {
        (Guid parcel, Guid flow, string branch, _) = await ArrangeAsync();
        string error = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"remote line {i:D2}")) + "\nfatal: unable to access 'https://someone:secret@github.com/example/repo.git/'\n";
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(128, error));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel, flow));

        paused.Lever.Should().Be(FlowLevers.PerformAgain("rebase"));
        paused.Reason.Should().StartWith("Step 'rebase' could not push").And.Contain("remote line 30").And.NotContain("remote line 11").And.NotContain("secret");
        runner.Branch(Clone, branch).Should().Be(Recorded);

        runner.On(c => c.IsGit("push"), (command, _) => Task.FromResult(ScriptedCommandRunner.Ok()));
        runner.SetRemoteBranch(Clone, branch, Recorded);
        int before = runner.Commands.Count;
        JsonElement again = Done(await PerformAsync(parcel, flow));

        again.GetProperty("head").GetString().Should().Be(Rebased);
        runner.GitCalls.Skip(before).Should().Contain($"checkout --quiet --detach {Recorded}")
            .And.Contain($"push --force-with-lease=refs/heads/{branch}:{Recorded} origin {Rebased}:refs/heads/{branch}");
    }

    [Fact]
    public async Task A_rebase_that_finished_and_was_not_pushed_is_performed_again_from_the_recorded_head()
    {
        (Guid parcel, Guid flow, string branch, string workPath) = await ArrangeAsync();
        bool interrupted = false;
        runner.On(c => c.IsGit("push") && !interrupted, (_, _) =>
        {
            interrupted = true;
            throw new OperationCanceledException("The daemon stopped.");
        });

        Func<Task> first = async () => await PerformAsync(parcel, flow);
        await first.Should().ThrowAsync<OperationCanceledException>();
        runner.Branch(Clone, branch).Should().Be(Recorded);
        runner.CheckedOut(Clone, workPath).Should().BeNull("the rebase ran on a detached head");

        int before = runner.Commands.Count;
        JsonElement result = Done(await PerformAsync(parcel, flow));

        result.GetProperty("head").GetString().Should().Be(Rebased);
        runner.GitCalls.Skip(before).Should().Contain($"-c rebase.updateRefs=false rebase --quiet {ScriptedCommandRunner.PrimaryHead}");
        runner.Branch(Clone, branch).Should().Be(Rebased);
        runner.RemoteBranch(Clone, branch).Should().Be(Rebased);
    }

    [Fact]
    public async Task A_push_that_was_not_recorded_is_answered_with_the_noted_commit_and_pushes_nothing_again()
    {
        (Guid parcel, Guid flow, string branch, string workPath) = await ArrangeAsync();
        bool interrupted = false;
        runner.On(c => c.IsGit("update-ref", $"refs/heads/{branch}") && !interrupted, (_, _) =>
        {
            interrupted = true;
            throw new OperationCanceledException("The daemon stopped.");
        });

        Func<Task> first = async () => await PerformAsync(parcel, flow);
        await first.Should().ThrowAsync<OperationCanceledException>();
        runner.RemoteBranch(Clone, branch).Should().Be(Rebased, "the push went through before the stop");
        runner.Branch(Clone, branch).Should().Be(Recorded);

        int before = runner.Commands.Count;
        JsonElement result = Done(await PerformAsync(parcel, flow));

        result.GetProperty("head").GetString().Should().Be(Rebased);
        runner.GitCalls.Skip(before).Should().NotContain(c => c.StartsWith("push", StringComparison.Ordinal) || c.StartsWith("fetch", StringComparison.Ordinal) || c.Contains(" rebase --quiet", StringComparison.Ordinal));
        runner.Branch(Clone, branch).Should().Be(Rebased);
        runner.CheckedOut(Clone, workPath).Should().Be(branch);
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Rebased);

        // Once the branch has moved too, performing it again still answers the noted commit.
        before = runner.Commands.Count;
        Done(await PerformAsync(parcel, flow)).GetProperty("head").GetString().Should().Be(Rebased);
        runner.GitCalls.Skip(before).Should().NotContain(c => c.StartsWith("push", StringComparison.Ordinal) || c.StartsWith("update-ref refs/heads/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rebase_left_in_progress_is_aborted_before_the_action_starts_again()
    {
        (Guid parcel, Guid flow, string branch, _) = await ArrangeAsync();
        runner.ConflictOnRebase("src/Program.cs");
        bool interrupted = false;
        runner.On(c => c.IsGit("diff") && !interrupted, (_, _) =>
        {
            interrupted = true;
            throw new OperationCanceledException("The daemon stopped.");
        });
        Func<Task> first = async () => await PerformAsync(parcel, flow);
        await first.Should().ThrowAsync<OperationCanceledException>();
        runner.ConflictOnRebase();

        int before = runner.Commands.Count;
        JsonElement result = Done(await PerformAsync(parcel, flow));

        GitCallAt(before).Should().Be("rebase --abort");
        result.GetProperty("head").GetString().Should().Be(Rebased);
        runner.Branch(Clone, branch).Should().Be(Rebased);
    }

    [Theory]
    [InlineData("fatal: no rebase in progress\n")]
    [InlineData("fatal: No rebase in progress?\n")]
    public async Task An_abort_that_finds_no_rebase_in_progress_in_either_wording_git_has_used_lets_the_action_go_on(string wording)
    {
        (Guid parcel, Guid flow, _, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("rebase", "--abort"), ScriptedCommandRunner.Fails(128, wording));

        Done(await PerformAsync(parcel, flow));
    }

    [Fact]
    public async Task An_abort_that_fails_any_other_way_pauses_to_perform_again_before_anything_else_runs()
    {
        (Guid parcel, Guid flow, _, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("rebase", "--abort"), ScriptedCommandRunner.Fails(128, "fatal: cannot lock ref 'HEAD'\n"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel, flow));

        paused.Lever.Should().Be(FlowLevers.PerformAgain("rebase"));
        paused.Reason.Should().Contain("cannot lock ref");
        runner.GitCalls.Should().Equal("rebase --abort");
    }

    [Fact]
    public async Task A_chain_with_no_recorded_head_pauses_to_perform_again_and_runs_nothing()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Rebase me", null, None);
        Flow started = await flows.StartAsync(Principal.LocalOwner, Project, parcel.Id, Recorder.Name, None);

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, started.Id));

        paused.Reason.Should().Contain("no open-pr or rebase result with a head is recorded");
        paused.Lever.Should().Be(FlowLevers.PerformAgain("rebase"));
        runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_git_command_pauses_to_perform_again_quoting_it()
    {
        (Guid parcel, Guid flow, _, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("fetch"), ScriptedCommandRunner.Fails(128, "fatal: could not read from remote repository\n"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel, flow));

        paused.Lever.Should().Be(FlowLevers.PerformAgain("rebase"));
        paused.Reason.Should().StartWith("Step 'rebase' could not rebase").And.Contain("could not read from remote repository");
        runner.GitCalls.Should().NotContain(c => c.StartsWith("push", StringComparison.Ordinal));
    }

    /// <summary>
    /// A task with an open-pr result recording <see cref="Recorded"/>, and the clone holding its
    /// branch at that commit, checked out in the flow's worktree, with the remote's branch at it too.
    /// </summary>
    private async Task<(Guid Parcel, Guid Flow, string Branch, string WorkPath)> ArrangeAsync()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Rebase me", null, None);
        Flow started = await flows.StartAsync(Principal.LocalOwner, Project, parcel.Id, Recorder.Name, None);
        JsonElement result = JsonSerializer.SerializeToElement(new { verdict = "opened", action = SoftwareActions.OpenPr, number = 17, head = Recorded });
        await flows.RecordResultAsync(Principal.LocalOwner, Project, started.Id, "open-pr", SoftwareActions.OpenPr, result, None);
        string branch = ProjectClones.BranchOf(parcel.Id);
        string workPath = temp.Home.FlowWorkPath(Project, started.Id);
        runner.AddBranch(Clone, branch, Recorded);
        runner.AddWorktree(Clone, workPath, branch, Recorded);
        runner.SetRemoteBranch(Clone, branch, Recorded);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), Recorded);
        return (parcel.Id, started.Id, branch, workPath);
    }

    private ValueTask<ActionOutcome> PerformAsync(Guid parcel, Guid flow) =>
        action.PerformAsync(new ActionRequest(FlowPrincipal, Project, flow, parcel, Recorder, Step, false), None);

    private void NothingFetchedRebasedOrPushed() =>
        runner.GitCalls.Should().NotContain(c =>
            c.StartsWith("fetch", StringComparison.Ordinal)
            || c.Contains(" rebase --quiet", StringComparison.Ordinal)
            || c.StartsWith("push", StringComparison.Ordinal)
            || c.StartsWith("checkout", StringComparison.Ordinal)
            || c.StartsWith("update-ref", StringComparison.Ordinal));

    private string GitCallAt(int index) => runner.GitCalls[index];

    private static int IndexOf(IReadOnlyList<string> calls, string prefix) =>
        calls.Select((c, i) => (c, i)).Where(p => p.c.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p.i).DefaultIfEmpty(-1).First();

    private static JsonElement Done(ActionOutcome outcome)
    {
        JsonElement result = outcome.Should().BeOfType<ActionOutcome.Done>().Subject.Result;
        result.GetProperty("verdict").GetString().Should().Be("done");
        result.GetProperty("action").GetString().Should().Be(SoftwareActions.Rebase);
        return result;
    }

    private static ActionOutcome.Paused Paused(ActionOutcome outcome) =>
        outcome.Should().BeOfType<ActionOutcome.Paused>().Subject;
}
