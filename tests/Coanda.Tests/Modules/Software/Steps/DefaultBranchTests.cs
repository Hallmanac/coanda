using System.Text.Json;
using Coanda.Core.Parcels;
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
/// The primary branch a step acts on when the software module's <c>primary-branch</c> setting names none
/// for the repo: the branch the repo's remote reports as its default, read when the step runs. Real git
/// runs against a local bare repository standing in for the remote, a working repository beside it that
/// pushes to it, and a bare clone at the path where the core keeps it; the daemon runs on the scripted
/// runner, so only the step's own module, a fresh one over the same home, runs git. Nothing contacts any
/// other remote.
/// </summary>
public sealed class DefaultBranchTests : IAsyncLifetime
{
    private const string Fresh = "fresh";
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
    private string main = null!;
    private string trunk = null!;

    public async Task InitializeAsync()
    {
        node = await SoftwareNode.StartAsync();
        module = node.FreshModule(Runner);
        await node.Projects.AddAsync(Principal.LocalOwner, Fresh, RegisteredProjects.Remote, [], None);
        await Publishing.FlowAsync(node.Daemon, Fresh, SoftwareNode.Steps);

        remote = Path.Combine(node.Home.Path, "remotes", $"{RegisteredProjects.Repo}.git");
        pusher = Path.Combine(node.Home.Path, "pusher");
        Directory.CreateDirectory(remote);
        Directory.CreateDirectory(pusher);
        await GitAsync(remote, "init", "--bare", "--quiet");
        await GitAsync(pusher, "init", "--quiet");

        // main and trunk hold different commits, and the remote's HEAD names trunk
        main = await CommitAsync("main.txt", "on main");
        await GitAsync(pusher, "push", "--quiet", remote, $"{main}:refs/heads/main");
        trunk = await CommitAsync("trunk.txt", "on trunk");
        await GitAsync(pusher, "push", "--quiet", remote, $"{trunk}:refs/heads/trunk");
        await GitAsync(remote, "symbolic-ref", "HEAD", "refs/heads/trunk");

        // The scripted runner's clones left empty directories, which git makes repositories in.
        foreach (string project in new[] { SoftwareNode.Project, Fresh })
        {
            await CloneAsync(project, remote);
        }
    }

    public async Task DisposeAsync()
    {
        await module.DisposeAsync();
        await node.DisposeAsync();
    }

    [Fact]
    public async Task A_project_registered_with_no_primary_branch_cuts_its_branch_from_the_remotes_default_branch()
    {
        JsonElement result = Done(await WorktreeAsync(Fresh));

        Assert.Equal(trunk, result.GetProperty("head").GetString());
    }

    [Fact]
    public async Task A_repo_the_setting_names_a_branch_for_still_uses_the_named_branch()
    {
        // the default project's registration names main for its repo
        JsonElement result = Done(await WorktreeAsync(SoftwareNode.Project));

        Assert.Equal(main, result.GetProperty("head").GetString());
    }

    [Fact]
    public async Task A_remote_whose_head_names_no_branch_pauses_with_the_lever_that_names_the_setting()
    {
        await GitAsync(remote, "update-ref", "--no-deref", "HEAD", trunk);

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(await WorktreeAsync(Fresh));

        Assert.Equal(
            "Step 'worktree' has no primary branch for repo 'repo': the software module's setting 'primary-branch' names none for it in project 'fresh', and the repo's remote reports no default branch: its HEAD names no branch.",
            paused.Reason);
        Assert.Equal(
            """Give the software module's setting 'primary-branch' a value in project 'fresh' (coa project module set fresh software primary-branch '{"repo":"<branch>"}'), then resume the flow run to perform step 'worktree' again.""",
            paused.Lever);
    }

    [Theory]
    [InlineData("odd$name")]
    [InlineData("semi;colon")]
    [InlineData("tick`name")]
    public async Task A_remote_whose_default_branch_holds_a_character_outside_the_accepted_set_pauses_with_the_lever_that_names_the_setting(string branch)
    {
        // git itself accepts each of these names
        await GitAsync(pusher, "check-ref-format", "--branch", branch);
        await GitAsync(pusher, "push", "--quiet", remote, $"{trunk}:refs/heads/{branch}");
        await GitAsync(remote, "symbolic-ref", "HEAD", $"refs/heads/{branch}");

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(await WorktreeAsync(Fresh));

        Assert.Equal(
            "Step 'worktree' has no primary branch for repo 'repo': the software module's setting 'primary-branch' names none for it in project 'fresh', and the repo's remote reports a default branch whose name the step does not act on: a name read from the remote must pass git check-ref-format --branch and hold only ASCII letters, digits, '.', '_', '-', and '/'.",
            paused.Reason);
        Assert.Contains("coa project module set fresh software primary-branch", paused.Lever, StringComparison.Ordinal);
        Assert.DoesNotContain(branch, paused.Reason + paused.Lever, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_remote_that_cannot_be_reached_pauses_with_the_perform_again_lever_quoting_git()
    {
        string missing = Path.Combine(node.Home.Path, "remotes", "missing.git");
        await GitAsync(node.Home.RepoClonePath(Fresh, RegisteredProjects.Repo), "remote", "set-url", Git.Remote, missing);

        SystemStepAnswer.Paused paused = Assert.IsType<SystemStepAnswer.Paused>(await WorktreeAsync(Fresh));

        Assert.StartsWith(
            "Step 'worktree' has no primary branch for repo 'repo': the software module's setting 'primary-branch' names none for it in project 'fresh', and the default branch of the repo's remote could not be read: git ",
            paused.Reason,
            StringComparison.Ordinal);
        Assert.Contains("ls-remote --symref origin HEAD exited with code 128", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(missing, paused.Reason, StringComparison.Ordinal);
        Assert.Equal("Resume the flow run to perform step 'worktree' again.", paused.Lever);
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/2.0_rc-1", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("café", false)]
    [InlineData("x|y", false)]
    [InlineData("x&y", false)]
    public void Only_ascii_letters_digits_dot_underscore_hyphen_and_slash_are_accepted_from_a_remote(string branch, bool accepted) =>
        Assert.Equal(accepted, ProjectClones.Accepts(branch));

    /// <summary>Starts a flow run on a new task in <paramref name="project"/> held at the worktree step and dispatches the step to the fresh module.</summary>
    private async Task<SystemStepAnswer> WorktreeAsync(string project)
    {
        Parcel task = await node.TaskAsync("Cut a branch", project);
        (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(task.Id, "worktree", project);
        return await node.DispatchAsync(SoftwareSteps.Worktree, task.Id, flowRunId, dispatchId, project, module: module);
    }

    /// <summary>Makes the core's clone of <paramref name="project"/>'s repo a bare repository whose remote is <paramref name="origin"/>.</summary>
    private async Task CloneAsync(string project, string origin)
    {
        string clone = node.Home.RepoClonePath(project, RegisteredProjects.Repo);
        Directory.CreateDirectory(clone);
        await GitAsync(clone, "init", "--bare", "--quiet");
        await GitAsync(clone, "remote", "add", Git.Remote, origin);
    }

    /// <summary>Commits <paramref name="content"/> as <paramref name="file"/> in the pusher's working tree and returns the commit.</summary>
    private async Task<string> CommitAsync(string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(pusher, file), content + "\n");
        await GitAsync(pusher, "add", "--", file);
        await GitAsync(pusher, "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", $"Add {file}");
        return await GitAsync(pusher, "rev-parse", "HEAD");
    }

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
        return result;
    }
}
