using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.FlowRuns;

namespace Coanda.Tests.Modules.Software.Steps;

/// <summary>
/// The review diff step against real git: a local bare repository under the node's temporary home
/// stands in for the remote, a working repository beside it stands in for the people pushing to it, and
/// the clone is a bare repository at the path where the core keeps it, fetching from that remote, as
/// <see cref="Coanda.Tests.Daemon.Transport.LedgerNode"/> makes its clones. The daemon runs on the
/// scripted runner, so only the step's own module, a fresh one over the same home, runs git. Every
/// branch is made in those temporary repositories, and nothing contacts any other remote.
/// </summary>
public sealed class ReviewDiffStepTests : IAsyncLifetime
{
    private const string Project = SoftwareNode.Project;
    private const string Primary = RegisteredProjects.Branch;
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly ProcessCommandRunner Runner = new();

    /// <summary>The variables the tests' own git commands run with: git's own, and an identity for their commits.</summary>
    private static readonly IReadOnlyDictionary<string, string> Variables = new Dictionary<string, string>(Git.Environment)
    {
        ["GIT_AUTHOR_NAME"] = "test",
        ["GIT_AUTHOR_EMAIL"] = "test@example.com",
        ["GIT_COMMITTER_NAME"] = "test",
        ["GIT_COMMITTER_EMAIL"] = "test@example.com",
    };

    private SoftwareNode node = null!;
    private SoftwareModule module = null!;
    private string remote = null!;
    private string pusher = null!;
    private string clone = null!;

    public async Task InitializeAsync()
    {
        node = await SoftwareNode.StartAsync();
        module = node.FreshModule(Runner);
        remote = Path.Combine(node.Home.Path, "remotes", Project, $"{RegisteredProjects.Repo}.git");
        pusher = Path.Combine(node.Home.Path, "pusher");
        clone = node.Home.RepoClonePath(Project, RegisteredProjects.Repo);

        // The scripted runner's clone left an empty directory there, which git makes a repository in.
        Directory.CreateDirectory(remote);
        Directory.CreateDirectory(pusher);
        Directory.CreateDirectory(clone);
        await GitAsync(remote, "init", "--bare", "--quiet");
        await GitAsync(pusher, "init", "--quiet");
        await GitAsync(pusher, "checkout", "--quiet", "-b", Primary);
        await GitAsync(clone, "init", "--bare", "--quiet");
        await GitAsync(clone, "remote", "add", Git.Remote, remote);
    }

    public async Task DisposeAsync()
    {
        await module.DisposeAsync();
        await node.DisposeAsync();
    }

    [Fact]
    public async Task A_change_committed_in_the_worktree_appears_in_the_file_and_the_result_names_the_commits_diffed()
    {
        string cut = await PushPrimaryAsync("base.txt", "the base line");
        (Guid parcel, Guid flowRun, string work) = await CutAsync(cut);
        string head = await CommitAsync(work, "feature.txt", "the feature line");

        JsonElement result = Done(await DispatchAsync(parcel, flowRun));

        Assert.Equal(cut, result.GetProperty("base").GetString());
        Assert.Equal(head, result.GetProperty("head").GetString());
        Assert.False(result.TryGetProperty("reason", out _));
        string diff = Diff(work);
        Assert.Contains("+++ b/feature.txt", diff, StringComparison.Ordinal);
        Assert.Contains("+the feature line", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("base.txt", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_primary_branch_commit_made_after_the_cut_and_fetched_into_the_tracking_ref_does_not_appear()
    {
        string cut = await PushPrimaryAsync("base.txt", "the base line");
        (Guid parcel, Guid flowRun, string work) = await CutAsync(cut);
        await CommitAsync(work, "feature.txt", "the feature line");
        string later = await PushPrimaryAsync("later.txt", "a later primary line");
        await GitAsync(clone, "fetch", "--quiet", Git.Remote);
        Assert.Equal(later, await RefAsync(ProjectClones.TrackingRefOf(Primary)));

        JsonElement result = Done(await DispatchAsync(parcel, flowRun));

        Assert.Equal(cut, result.GetProperty("base").GetString());
        string diff = Diff(work);
        Assert.Contains("+the feature line", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("later.txt", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("a later primary line", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Primary_branch_commits_between_an_older_tracking_ref_and_the_cut_do_not_appear()
    {
        string older = await PushPrimaryAsync("base.txt", "the base line");
        await GitAsync(clone, "fetch", "--quiet", Git.Remote);
        string cut = await PushPrimaryAsync("between.txt", "a line between the two");
        Guid parcel = Ids.New();
        string branch = ProjectClones.BranchOf(parcel);

        // The branch was cut from the newer commit on another node, so this one fetches only the branch.
        await GitAsync(pusher, "push", "--quiet", remote, $"{cut}:refs/heads/{branch}");
        await GitAsync(clone, "fetch", "--quiet", Git.Remote, $"+refs/heads/{branch}:{ProjectClones.TrackingRefOf(branch)}");
        (Guid flowRun, string work) = await WorktreeAsync(parcel, ProjectClones.TrackingRefOf(branch));
        await CommitAsync(work, "feature.txt", "the feature line");
        Assert.Equal(older, await RefAsync(ProjectClones.TrackingRefOf(Primary)));

        JsonElement result = Done(await DispatchAsync(parcel, flowRun));

        Assert.Equal(cut, result.GetProperty("base").GetString());
        Assert.Equal(cut, await RefAsync(ProjectClones.TrackingRefOf(Primary)));
        string diff = Diff(work);
        Assert.Contains("+the feature line", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("between.txt", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("a line between the two", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_commit_the_tracking_ref_for_the_parcels_branch_holds_and_the_head_does_not_does_not_appear()
    {
        string cut = await PushPrimaryAsync("base.txt", "the base line");
        (Guid parcel, Guid flowRun, string work) = await CutAsync(cut);
        string head = await CommitAsync(work, "feature.txt", "the feature line");
        string branch = ProjectClones.BranchOf(parcel);
        await GitAsync(pusher, "checkout", "--quiet", "-b", "elsewhere", cut);
        string elsewhere = await CommitAsync(pusher, "elsewhere.txt", "a line only the remote's branch holds");
        await GitAsync(pusher, "push", "--quiet", remote, $"{elsewhere}:refs/heads/{branch}");
        await GitAsync(clone, "fetch", "--quiet", Git.Remote, $"+refs/heads/{branch}:{ProjectClones.TrackingRefOf(branch)}");
        Assert.Equal(elsewhere, await RefAsync(ProjectClones.TrackingRefOf(branch)));

        JsonElement result = Done(await DispatchAsync(parcel, flowRun));

        Assert.Equal(head, result.GetProperty("head").GetString());
        string diff = Diff(work);
        Assert.Contains("+the feature line", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("elsewhere.txt", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("a line only the remote's branch holds", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_run_after_a_further_commit_writes_a_file_holding_that_commits_change_too()
    {
        string cut = await PushPrimaryAsync("base.txt", "the base line");
        (Guid parcel, Guid flowRun, string work) = await CutAsync(cut);
        await CommitAsync(work, "feature.txt", "the feature line");
        Done(await DispatchAsync(parcel, flowRun));
        Assert.DoesNotContain("the fix line", Diff(work), StringComparison.Ordinal);
        string head = await CommitAsync(work, "fix.txt", "the fix line");

        JsonElement result = Done(await DispatchAsync(parcel, flowRun));

        Assert.Equal((cut, head), (result.GetProperty("base").GetString(), result.GetProperty("head").GetString()));
        string diff = Diff(work);
        Assert.Contains("+the feature line", diff, StringComparison.Ordinal);
        Assert.Contains("+the fix line", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_primary_branch_the_remote_does_not_hold_answers_done_saying_no_diff_was_written_and_removes_an_earlier_one()
    {
        string cut = await PushPrimaryAsync("base.txt", "the base line");
        (Guid parcel, Guid flowRun, string work) = await CutAsync(cut);
        await CommitAsync(work, "feature.txt", "the feature line");
        Done(await DispatchAsync(parcel, flowRun));
        Assert.True(File.Exists(Path.Combine(work, ".coanda", "review.diff")));
        await node.SetAsync(Project, SoftwareSettings.PrimaryBranch, new Dictionary<string, string> { [RegisteredProjects.Repo] = "absent" });

        JsonElement result = Done(await DispatchAsync(parcel, flowRun));

        string reason = result.GetProperty("reason").GetString() ?? string.Empty;
        Assert.StartsWith("Wrote no .coanda/review.diff: ", reason, StringComparison.Ordinal);
        Assert.Contains("refs/heads/absent", reason, StringComparison.Ordinal);
        Assert.False(result.TryGetProperty("base", out _));
        Assert.False(result.TryGetProperty("head", out _));
        Assert.False(File.Exists(Path.Combine(work, ".coanda", "review.diff")), "no review.diff from an earlier pass survives a failed one");
    }

    [Fact]
    public async Task A_link_left_at_the_file_or_at_coanda_is_replaced_never_written_or_deleted_through()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating a link there needs a privilege the test runner may not hold.
        }

        string cut = await PushPrimaryAsync("base.txt", "the base line");
        (Guid parcel, Guid flowRun, string work) = await CutAsync(cut);
        await CommitAsync(work, "feature.txt", "the feature line");
        string outside = Path.Combine(node.Home.Path, "outside");
        Directory.CreateDirectory(outside);
        string victim = Path.Combine(outside, "victim.md");
        File.WriteAllText(victim, "untouched");
        Directory.CreateDirectory(Path.Combine(work, ".coanda"));
        File.CreateSymbolicLink(Path.Combine(work, ".coanda", "review.diff"), victim);

        Done(await DispatchAsync(parcel, flowRun));

        Assert.Equal("untouched", File.ReadAllText(victim));
        Assert.Null(new FileInfo(Path.Combine(work, ".coanda", "review.diff")).LinkTarget);
        Assert.Contains("+the feature line", Diff(work), StringComparison.Ordinal);

        Directory.Delete(Path.Combine(work, ".coanda"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(work, ".coanda"), outside);
        Done(await DispatchAsync(parcel, flowRun));

        Assert.False(File.Exists(Path.Combine(outside, "review.diff")), "the linked directory is never written into");
        Assert.Null(new DirectoryInfo(Path.Combine(work, ".coanda")).LinkTarget);
        Assert.Contains("+the feature line", Diff(work), StringComparison.Ordinal);

        // A failed pass removes the link it finds at the file, not the file the link points at.
        File.Delete(Path.Combine(work, ".coanda", "review.diff"));
        File.CreateSymbolicLink(Path.Combine(work, ".coanda", "review.diff"), victim);
        await node.SetAsync(Project, SoftwareSettings.PrimaryBranch, new Dictionary<string, string> { [RegisteredProjects.Repo] = "absent" });
        Assert.True(Done(await DispatchAsync(parcel, flowRun)).TryGetProperty("reason", out _));

        Assert.Equal("untouched", File.ReadAllText(victim));
        Assert.False(File.Exists(Path.Combine(work, ".coanda", "review.diff")));
        Assert.Null(new FileInfo(Path.Combine(work, ".coanda", "review.diff")).LinkTarget);
    }

    [Fact]
    public async Task A_repo_with_no_primary_branch_pauses_naming_the_setting_and_writes_nothing()
    {
        await node.Projects.AddAsync(Principal.LocalOwner, "bare", RegisteredProjects.Remote, [], None);
        await Publishing.FlowAsync(node.Daemon, "bare", SoftwareNode.Steps);
        Guid parcel = (await node.TaskAsync("No branch", "bare")).Id;
        Guid flowRun = (await node.HeldAsync(parcel, "review-diff", "bare")).FlowRunId;

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(
            await node.DispatchAsync(SoftwareSteps.ReviewDiff, parcel, flowRun, project: "bare", module: module));

        Assert.Equal("Step 'review-diff' has no primary branch for repo 'repo': the software module's setting 'primary-branch' names none for it in project 'bare'.", paused.Reason);
        Assert.Equal(
            """Give the software module's setting 'primary-branch' a value in project 'bare' (coa project module set bare software primary-branch '{"repo":"<branch>"}'), then resume the flow run to perform step 'review-diff' again.""",
            paused.Lever);
        Assert.False(Directory.Exists(node.Home.FlowRunWorkPath("bare", flowRun)));
    }

    private Task<SystemStepAnswer> DispatchAsync(Guid parcel, Guid flowRun) =>
        node.DispatchAsync(SoftwareSteps.ReviewDiff, parcel, flowRun, module: module);

    /// <summary>Commits <paramref name="content"/> as <paramref name="file"/> on the primary branch and pushes it to the remote, returning the commit.</summary>
    private async Task<string> PushPrimaryAsync(string file, string content)
    {
        string commit = await CommitAsync(pusher, file, content);
        await GitAsync(pusher, "push", "--quiet", remote, $"{commit}:refs/heads/{Primary}");
        return commit;
    }

    /// <summary>Fetches the remote into the clone and cuts a new parcel's branch from <paramref name="from"/>, checked out in a flow run's worktree.</summary>
    private async Task<(Guid Parcel, Guid FlowRun, string Work)> CutAsync(string from)
    {
        await GitAsync(clone, "fetch", "--quiet", Git.Remote);
        Guid parcel = Ids.New();
        (Guid flowRun, string work) = await WorktreeAsync(parcel, from);
        return (parcel, flowRun, work);
    }

    /// <summary>Makes the parcel's branch at <paramref name="from"/> in the clone and checks it out at a new flow run's work path, as the worktree step does.</summary>
    private async Task<(Guid FlowRun, string Work)> WorktreeAsync(Guid parcel, string from)
    {
        Guid flowRun = Ids.New();
        string work = node.Home.FlowRunWorkPath(Project, flowRun);
        string branch = ProjectClones.BranchOf(parcel);
        await GitAsync(clone, "branch", "--no-track", branch, from);
        await GitAsync(clone, "worktree", "add", "--quiet", work, branch);
        return (flowRun, work);
    }

    /// <summary>Writes <paramref name="content"/> as <paramref name="file"/> in the working tree at <paramref name="directory"/>, commits it, and returns the commit.</summary>
    private static async Task<string> CommitAsync(string directory, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, file), content + "\n");
        await GitAsync(directory, "add", "--", file);
        await GitAsync(directory, "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", $"Add {file}");
        return await GitAsync(directory, "rev-parse", "HEAD");
    }

    private async Task<string> RefAsync(string reference) =>
        await GitAsync(clone, "rev-parse", "--verify", "--quiet", reference);

    private static string Diff(string work) => File.ReadAllText(Path.Combine(work, ".coanda", "review.diff"));

    /// <summary>Runs git in <paramref name="directory"/>, or against it when it is a bare repository, and returns its trimmed output; a failure fails the test.</summary>
    private static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var command = new CommandLine("git", arguments, directory, Variables) { Limit = Git.Limit };
        CommandResult result = await Runner.RunAsync(command, None);
        Assert.True(result.Succeeded, $"{command.Display} exited with code {result.ExitCode}: {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private static JsonElement Done(SystemStepAnswer outcome)
    {
        JsonElement result = Assert.IsType<SystemStepAnswer.Done>(outcome).Result;
        Assert.Equal("done", result.GetProperty("verdict").GetString());
        Assert.Equal(SoftwareSteps.ReviewDiff, result.GetProperty("systemStep").GetString());
        return result;
    }
}
