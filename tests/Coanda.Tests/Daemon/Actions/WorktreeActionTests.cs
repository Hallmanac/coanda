using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Commands;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;
using Coanda.Tests.Daemon.Commands;
using Fisher;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.Actions;

public sealed class WorktreeActionTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal FlowPrincipal = new("flow:under-test", Role.Agent);
    private static readonly ContourStep Step = new() { Name = "worktree", Action = SoftwareActions.Worktree };

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private IDocumentStore store = null!;
    private WorktreeAction action = null!;

    private string Clone => temp.Home.ProjectClonePath(Project);

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        action = new WorktreeAction(
            new ProjectService(store, TimeProvider.System),
            new ProjectClones(temp.Home, runner, NullLogger<ProjectClones>.Instance));
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
            $"fetch --no-tags --quiet origin +refs/heads/{RegisteredProjects.Branch}:refs/remotes/origin/{RegisteredProjects.Branch}",
            $"rev-parse --verify --quiet refs/remotes/origin/{RegisteredProjects.Branch}^{{commit}}",
            $"ls-tree -r -z --name-only {ScriptedCommandRunner.PrimaryHead} -- .coanda",
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
    public async Task A_branch_an_earlier_flow_on_the_parcel_created_is_checked_out_as_it_stands_with_nothing_fetched()
    {
        Guid parcelId = Ids.New();
        string branch = $"coanda/{Ids.Short(parcelId)}";
        await PerformAsync(parcelId, Ids.New());
        const string Moved = "fedcba9876543210fedcba9876543210fedcba98";
        runner.AddBranch(Clone, branch, Moved);
        int before = runner.Commands.Count;
        Guid later = Ids.New();

        JsonElement result = Done(await PerformAsync(parcelId, later));

        result.GetProperty("head").GetString().Should().Be(Moved, "the branch is checked out at its own tip, not the primary's");
        runner.GitCalls.Skip(before).Should().Equal(
            "rev-parse --is-bare-repository",
            "worktree list --porcelain",
            $"for-each-ref --format=%(objectname) refs/heads/{branch}",
            $"config --get --default  {ProjectClones.OwnerKeyOf(branch)}",
            $"ls-tree -r -z --name-only {Moved} -- .coanda",
            $"worktree add --quiet {temp.Home.FlowWorkPath(Project, later)} {branch}");
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
        runner.On(c => c.IsGit("ls-tree"), ScriptedCommandRunner.Ok(".coanda/task.md\0.coanda/notes/a.md\0"));

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
        runner.On(c => c.IsGit("ls-tree"), ScriptedCommandRunner.Ok(".coanda/na\u00efve \"x\".md\0"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(Ids.New(), Ids.New()));

        paused.Reason.Should().Contain(".coanda/na\u00efve \"x\".md");
    }

    [Fact]
    public async Task A_file_named_coanda_outside_the_directory_does_not_count_as_tracked_under_it()
    {
        runner.On(c => c.IsGit("ls-tree"), ScriptedCommandRunner.Ok(".coanda\0"));

        Done(await PerformAsync(Ids.New(), Ids.New()));
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
