using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;
using Coanda.Tests.Daemon.Commands;
using Fisher;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.SystemSteps;

/// <summary>
/// The rebase system step against the scripted command runner's model of the node's clone: the commands
/// it runs and their order, what it leaves the branch, the worktree, and the remote holding, and
/// each pause. The flow run it runs on is a one-step open-pr flow whose result the test records by
/// hand, so the recorded head is that result's.
/// </summary>
public sealed class RebaseSystemStepTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private const string Recorded = "aaaabbbbccccddddeeeeffff0000111122223333";
    private const string Moved = "fedcba9876543210fedcba9876543210fedcba98";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal FlowRunPrincipal = new("flow-run:under-test", Role.Agent);
    private static readonly FlowStep Step = new() { Name = "rebase", SystemStep = SoftwareSystemSteps.Rebase };

    /// <summary>A flow whose one step is open-pr, so a flow run of it can hold a recorded open-pr result.</summary>
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
                SystemStep = SoftwareSystemSteps.OpenPr,
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("opened"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private FlowRunService flowRuns = null!;
    private RebaseSystemStep systemStep = null!;

    private string Clone => temp.Home.ProjectClonePath(Project);

    private static string Rebased => ScriptedCommandRunner.RebasedOnto(Recorded, ScriptedCommandRunner.PrimaryHead);

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        parcels = new ParcelService(store, TimeProvider.System);
        flowRuns = new FlowRunService(store, TimeProvider.System);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, Project, Recorder.Name,
            Flow.CanonicalJson(Recorder), Recorder.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        systemStep = new RebaseSystemStep(
            new ProjectService(store, TimeProvider.System),
            new ProjectClones(temp.Home, runner, NullLogger<ProjectClones>.Instance),
            flowRuns);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task A_behind_branch_is_rebased_from_the_recorded_head_onto_the_fetched_primary_noted_pushed_under_its_lease_and_then_moved()
    {
        (Guid parcel, Guid flowRun, string branch, string workPath) = await ArrangeAsync();

        JsonElement result = Done(await PerformAsync(parcel, flowRun));

        Assert.Equal(Rebased, result.GetProperty("head").GetString());
        Assert.Equal(branch, result.GetProperty("branch").GetString());
        Assert.Equal(["rebase --abort", "status --porcelain --untracked-files=all", $"ls-remote --refs origin refs/heads/{branch}", $"for-each-ref --format=%(objectname) {ProjectClones.NoteRefOf(flowRun)}", $"for-each-ref --format=%(objectname) refs/heads/{branch}", $"fetch --no-tags --quiet origin +refs/heads/{RegisteredProjects.Branch}:refs/remotes/origin/{RegisteredProjects.Branch}", $"rev-parse --verify --quiet refs/remotes/origin/{RegisteredProjects.Branch}^{{commit}}", $"checkout --quiet --detach {Recorded}", $"-c rebase.updateRefs=false rebase --quiet {ScriptedCommandRunner.PrimaryHead}", "rev-parse --verify --quiet HEAD^{commit}", $"update-ref {ProjectClones.NoteRefOf(flowRun)} {Rebased}", $"push --force-with-lease=refs/heads/{branch}:{Recorded} origin {Rebased}:refs/heads/{branch}", $"checkout --quiet --detach {Rebased}", $"update-ref refs/heads/{branch} {Rebased} {Recorded}", $"checkout --quiet {branch}", $"update-ref {ProjectClones.TrackingRefOf(branch)} {Rebased}"], runner.GitCalls);
        Assert.All(runner.Commands.Where(c => c.GitDirectory() is null), c => Assert.True(c.WorkingDirectory == workPath, "the rebase works in the merge flow run's own worktree"));
        Assert.Equal(Rebased, runner.Branch(Clone, branch));
        Assert.Equal(branch, runner.CheckedOut(Clone, workPath));
        Assert.Equal(Rebased, runner.RemoteBranch(Clone, branch));
        Assert.Equal(Rebased, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
        Assert.Equal(Rebased, runner.TrackingRef(Clone, ProjectClones.NoteRefOf(flowRun)));
    }

    [Fact]
    public async Task No_command_that_moves_the_branch_runs_before_the_push_and_the_note_is_written_before_it()
    {
        (Guid parcel, Guid flowRun, string branch, _) = await ArrangeAsync();

        Done(await PerformAsync(parcel, flowRun));

        IReadOnlyList<string> calls = runner.GitCalls;
        int push = IndexOf(calls, "push ");
        int note = IndexOf(calls, $"update-ref {ProjectClones.NoteRefOf(flowRun)} ");
        int moved = IndexOf(calls, $"update-ref refs/heads/{branch} ");
        Assert.True(note < push, $"Expected the note's update-ref before the push at {push}, but it is at {note}.");
        Assert.True(moved > push, $"Expected the branch's update-ref after the push at {push}, but it is at {moved}.");
        Assert.All(calls.Select((c, i) => (c, i)).Where(p => p.c == $"checkout --quiet {branch}"), p => Assert.True(p.i > push));
    }

    [Fact]
    public async Task Every_command_runs_with_terminal_prompts_off_ssh_in_batch_mode_the_C_locale_and_the_git_limit()
    {
        (Guid parcel, Guid flowRun, _, _) = await ArrangeAsync();

        Done(await PerformAsync(parcel, flowRun));

        Assert.NotEmpty(runner.Commands);
        Assert.All(runner.Commands, c => Assert.True(c.Program == "git"
            && c.Environment["GIT_TERMINAL_PROMPT"] == "0"
            && c.Environment["GIT_SSH_COMMAND"].Contains("BatchMode=yes")
            && c.Environment["LC_ALL"] == "C"
            && c.Limit == Git.Limit));
    }

    [Fact]
    public async Task A_conflict_is_aborted_the_branch_checked_out_again_at_the_recorded_head_and_the_flow_run_paused_naming_the_unmerged_paths()
    {
        (Guid parcel, Guid flowRun, string branch, string workPath) = await ArrangeAsync();
        runner.ConflictOnRebase("src/Program.cs", "docs/a b.md");

        SystemStepOutcome.Paused paused = Paused(await PerformAsync(parcel, flowRun));

        Assert.StartsWith("Step 'rebase' rebased nothing", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("stopped on a conflict", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("src/Program.cs", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("docs/a b.md", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(Recorded, paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.RebaseConflict(Clone, branch, RegisteredProjects.Branch), paused.Lever);
        Assert.Contains("Stop the flow run", paused.Lever, StringComparison.Ordinal);
        Assert.Contains(Clone, paused.Lever, StringComparison.Ordinal);
        Assert.Contains("outside the daemon's work paths", paused.Lever, StringComparison.Ordinal);
        Assert.Contains($"rebase {branch}", paused.Lever, StringComparison.Ordinal);
        Assert.Contains("without pushing", paused.Lever, StringComparison.Ordinal);
        Assert.Contains("Remove that worktree", paused.Lever, StringComparison.Ordinal);
        Assert.Contains("move the task back to build", paused.Lever, StringComparison.Ordinal);
        Assert.DoesNotContain("merge commit", paused.Lever, StringComparison.Ordinal);
        Assert.DoesNotContain("Update branch", paused.Lever, StringComparison.Ordinal);
        SequenceAssert.ContainsInOrder(["rebase --abort", "-c rebase.updateRefs=false rebase --quiet " + ScriptedCommandRunner.PrimaryHead, "rebase --abort", $"checkout --quiet {branch}"], runner.GitCalls);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("push", StringComparison.Ordinal) || c.StartsWith("update-ref", StringComparison.Ordinal));
        Assert.Equal(Recorded, runner.Branch(Clone, branch));
        Assert.Equal(branch, runner.CheckedOut(Clone, workPath));
        Assert.Equal(Recorded, runner.RemoteBranch(Clone, branch));
    }

    [Fact]
    public async Task A_push_the_lease_refuses_pauses_with_the_stop_lever_saying_someone_else_updated_the_branch()
    {
        (Guid parcel, Guid flowRun, string branch, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(1, $" ! [rejected]        {Rebased} -> {branch} (stale info)\nerror: failed to push some refs\n"));

        SystemStepOutcome.Paused paused = Paused(await PerformAsync(parcel, flowRun));

        Assert.Contains("someone else updated branch", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("(stale info)", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.RebaseRefused("rebase"), paused.Lever);
        // the branch moves only once the push has succeeded
        Assert.Equal(Recorded, runner.Branch(Clone, branch));
    }

    [Fact]
    public async Task A_worktree_with_changes_pauses_with_the_stop_lever_before_any_fetch_rebase_or_push()
    {
        (Guid parcel, Guid flowRun, _, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("status"), ScriptedCommandRunner.Ok(" M src/Program.cs\n?? notes.txt\n"));

        SystemStepOutcome.Paused paused = Paused(await PerformAsync(parcel, flowRun));

        Assert.Contains("has changes", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("src/Program.cs", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("notes.txt", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.RebaseRefused("rebase"), paused.Lever);
        NothingFetchedRebasedOrPushed();
    }

    [Fact]
    public async Task A_branch_tip_that_is_not_the_recorded_head_pauses_with_the_stop_lever_before_any_fetch_rebase_or_push()
    {
        (Guid parcel, Guid flowRun, string branch, _) = await ArrangeAsync();
        runner.AddBranch(Clone, branch, Moved);

        SystemStepOutcome.Paused paused = Paused(await PerformAsync(parcel, flowRun));

        Assert.Contains($"is at {Moved}, not {Recorded}", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.RebaseRefused("rebase"), paused.Lever);
        NothingFetchedRebasedOrPushed();
    }

    [Fact]
    public async Task A_remote_branch_and_tip_holding_a_commit_the_step_did_not_note_pause_with_the_stop_lever_and_record_no_head()
    {
        (Guid parcel, Guid flowRun, string branch, _) = await ArrangeAsync();
        runner.AddBranch(Clone, branch, Moved);
        runner.SetRemoteBranch(Clone, branch, Moved);

        SystemStepOutcome outcome = await PerformAsync(parcel, flowRun);

        SystemStepOutcome.Paused paused = Paused(outcome);
        Assert.Equal(FlowRunLevers.RebaseRefused("rebase"), paused.Lever);
        Assert.Contains($"the remote's branch, at {Moved}, is no commit this step noted and pushed", paused.Reason, StringComparison.Ordinal);
        NothingFetchedRebasedOrPushed();
        Assert.Equal(Moved, runner.Branch(Clone, branch));
    }

    [Fact]
    public async Task A_push_that_fails_another_way_pauses_to_perform_again_quoting_twenty_lines_and_is_then_performed_again_from_the_recorded_head()
    {
        (Guid parcel, Guid flowRun, string branch, _) = await ArrangeAsync();
        string error = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"remote line {i:D2}")) + "\nfatal: unable to access 'https://someone:secret@github.com/example/repo.git/'\n";
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(128, error));

        SystemStepOutcome.Paused paused = Paused(await PerformAsync(parcel, flowRun));

        Assert.Equal(FlowRunLevers.PerformAgain("rebase"), paused.Lever);
        Assert.StartsWith("Step 'rebase' could not push", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("remote line 30", paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("remote line 11", paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(Recorded, runner.Branch(Clone, branch));

        runner.On(c => c.IsGit("push"), (command, _) => Task.FromResult(ScriptedCommandRunner.Ok()));
        runner.SetRemoteBranch(Clone, branch, Recorded);
        int before = runner.Commands.Count;
        JsonElement again = Done(await PerformAsync(parcel, flowRun));

        Assert.Equal(Rebased, again.GetProperty("head").GetString());
        IEnumerable<string> actual = runner.GitCalls.Skip(before);
        Assert.Contains($"checkout --quiet --detach {Recorded}", actual);
        Assert.Contains($"push --force-with-lease=refs/heads/{branch}:{Recorded} origin {Rebased}:refs/heads/{branch}", actual);
    }

    [Fact]
    public async Task A_rebase_that_finished_and_was_not_pushed_is_performed_again_from_the_recorded_head()
    {
        (Guid parcel, Guid flowRun, string branch, string workPath) = await ArrangeAsync();
        bool interrupted = false;
        runner.On(c => c.IsGit("push") && !interrupted, (_, _) =>
        {
            interrupted = true;
            throw new OperationCanceledException("The daemon stopped.");
        });

        Func<Task> first = async () => await PerformAsync(parcel, flowRun);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(first);
        Assert.Equal(Recorded, runner.Branch(Clone, branch));
        // the rebase ran on a detached head
        Assert.Null(runner.CheckedOut(Clone, workPath));

        int before = runner.Commands.Count;
        JsonElement result = Done(await PerformAsync(parcel, flowRun));

        Assert.Equal(Rebased, result.GetProperty("head").GetString());
        Assert.Contains($"-c rebase.updateRefs=false rebase --quiet {ScriptedCommandRunner.PrimaryHead}", runner.GitCalls.Skip(before));
        Assert.Equal(Rebased, runner.Branch(Clone, branch));
        Assert.Equal(Rebased, runner.RemoteBranch(Clone, branch));
    }

    [Fact]
    public async Task A_push_that_was_not_recorded_is_answered_with_the_noted_commit_and_pushes_nothing_again()
    {
        (Guid parcel, Guid flowRun, string branch, string workPath) = await ArrangeAsync();
        bool interrupted = false;
        runner.On(c => c.IsGit("update-ref", $"refs/heads/{branch}") && !interrupted, (_, _) =>
        {
            interrupted = true;
            throw new OperationCanceledException("The daemon stopped.");
        });

        Func<Task> first = async () => await PerformAsync(parcel, flowRun);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(first);
        // the push went through before the stop
        Assert.Equal(Rebased, runner.RemoteBranch(Clone, branch));
        Assert.Equal(Recorded, runner.Branch(Clone, branch));

        int before = runner.Commands.Count;
        JsonElement result = Done(await PerformAsync(parcel, flowRun));

        Assert.Equal(Rebased, result.GetProperty("head").GetString());
        Assert.DoesNotContain(runner.GitCalls.Skip(before), c => c.StartsWith("push", StringComparison.Ordinal) || c.StartsWith("fetch", StringComparison.Ordinal) || c.Contains(" rebase --quiet", StringComparison.Ordinal));
        Assert.Equal(Rebased, runner.Branch(Clone, branch));
        Assert.Equal(branch, runner.CheckedOut(Clone, workPath));
        Assert.Equal(Rebased, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));

        // Once the branch has moved too, performing it again still answers the noted commit.
        before = runner.Commands.Count;
        Assert.Equal(Rebased, Done(await PerformAsync(parcel, flowRun)).GetProperty("head").GetString());
        Assert.DoesNotContain(runner.GitCalls.Skip(before), c => c.StartsWith("push", StringComparison.Ordinal) || c.StartsWith("update-ref refs/heads/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rebase_left_in_progress_is_aborted_before_the_system_step_starts_again()
    {
        (Guid parcel, Guid flowRun, string branch, _) = await ArrangeAsync();
        runner.ConflictOnRebase("src/Program.cs");
        bool interrupted = false;
        runner.On(c => c.IsGit("diff") && !interrupted, (_, _) =>
        {
            interrupted = true;
            throw new OperationCanceledException("The daemon stopped.");
        });
        Func<Task> first = async () => await PerformAsync(parcel, flowRun);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(first);
        runner.ConflictOnRebase();

        int before = runner.Commands.Count;
        JsonElement result = Done(await PerformAsync(parcel, flowRun));

        Assert.Equal("rebase --abort", GitCallAt(before));
        Assert.Equal(Rebased, result.GetProperty("head").GetString());
        Assert.Equal(Rebased, runner.Branch(Clone, branch));
    }

    [Theory]
    [InlineData("fatal: no rebase in progress\n")]
    [InlineData("fatal: No rebase in progress?\n")]
    public async Task An_abort_that_finds_no_rebase_in_progress_in_either_wording_git_has_used_lets_the_system_step_go_on(string wording)
    {
        (Guid parcel, Guid flowRun, _, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("rebase", "--abort"), ScriptedCommandRunner.Fails(128, wording));

        Done(await PerformAsync(parcel, flowRun));
    }

    [Fact]
    public async Task An_abort_that_fails_any_other_way_pauses_to_perform_again_before_anything_else_runs()
    {
        (Guid parcel, Guid flowRun, _, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("rebase", "--abort"), ScriptedCommandRunner.Fails(128, "fatal: cannot lock ref 'HEAD'\n"));

        SystemStepOutcome.Paused paused = Paused(await PerformAsync(parcel, flowRun));

        Assert.Equal(FlowRunLevers.PerformAgain("rebase"), paused.Lever);
        Assert.Contains("cannot lock ref", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(["rebase --abort"], runner.GitCalls);
    }

    [Fact]
    public async Task A_chain_with_no_recorded_head_pauses_to_perform_again_and_runs_nothing()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Rebase me", null, None);
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, Recorder.Name, None);

        SystemStepOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, started.Id));

        Assert.Contains("no open-pr or rebase result with a head is recorded", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("rebase"), paused.Lever);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_failing_git_command_pauses_to_perform_again_quoting_it()
    {
        (Guid parcel, Guid flowRun, _, _) = await ArrangeAsync();
        runner.On(c => c.IsGit("fetch"), ScriptedCommandRunner.Fails(128, "fatal: could not read from remote repository\n"));

        SystemStepOutcome.Paused paused = Paused(await PerformAsync(parcel, flowRun));

        Assert.Equal(FlowRunLevers.PerformAgain("rebase"), paused.Lever);
        Assert.StartsWith("Step 'rebase' could not rebase", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("could not read from remote repository", paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("push", StringComparison.Ordinal));
    }

    /// <summary>
    /// A task with an open-pr result recording <see cref="Recorded"/>, and the clone holding its
    /// branch at that commit, checked out in the flow run's worktree, with the remote's branch at it too.
    /// </summary>
    private async Task<(Guid Parcel, Guid FlowRun, string Branch, string WorkPath)> ArrangeAsync()
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Rebase me", null, None);
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel.Id, Recorder.Name, None);
        JsonElement result = JsonSerializer.SerializeToElement(new { verdict = "opened", systemStep = SoftwareSystemSteps.OpenPr, number = 17, head = Recorded });
        await flowRuns.RecordResultAsync(Principal.LocalOwner, Project, started.Id, "open-pr", SoftwareSystemSteps.OpenPr, result, None);
        string branch = ProjectClones.BranchOf(parcel.Id);
        string workPath = temp.Home.FlowRunWorkPath(Project, started.Id);
        runner.AddBranch(Clone, branch, Recorded);
        runner.AddWorktree(Clone, workPath, branch, Recorded);
        runner.SetRemoteBranch(Clone, branch, Recorded);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), Recorded);
        return (parcel.Id, started.Id, branch, workPath);
    }

    private ValueTask<SystemStepOutcome> PerformAsync(Guid parcel, Guid flowRun) =>
        systemStep.PerformAsync(new SystemStepRequest(FlowRunPrincipal, Project, flowRun, parcel, Recorder, Step, false), None);

    private void NothingFetchedRebasedOrPushed()
    {
        Assert.DoesNotContain(runner.GitCalls, c =>
            c.StartsWith("fetch", StringComparison.Ordinal)
            || c.Contains(" rebase --quiet", StringComparison.Ordinal)
            || c.StartsWith("push", StringComparison.Ordinal)
            || c.StartsWith("checkout", StringComparison.Ordinal)
            || c.StartsWith("update-ref", StringComparison.Ordinal));
    }

    private string GitCallAt(int index) => runner.GitCalls[index];

    private static int IndexOf(IReadOnlyList<string> calls, string prefix) =>
        calls.Select((c, i) => (c, i)).Where(p => p.c.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p.i).DefaultIfEmpty(-1).First();

    private static JsonElement Done(SystemStepOutcome outcome)
    {
        JsonElement result = Assert.IsType<SystemStepOutcome.Done>(outcome).Result;
        Assert.Equal("done", result.GetProperty("verdict").GetString());
        Assert.Equal(SoftwareSystemSteps.Rebase, result.GetProperty("systemStep").GetString());
        return result;
    }

    private static SystemStepOutcome.Paused Paused(SystemStepOutcome outcome) => Assert.IsType<SystemStepOutcome.Paused>(outcome);
}
