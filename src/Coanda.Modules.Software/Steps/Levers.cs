namespace Coanda.Modules.Software.Steps;

/// <summary>The levers the module's pauses carry: what a person acts on to get a paused flow run moving again.</summary>
public static class Levers
{
    /// <summary>Names the step a resume performs again, in the words the core's own pauses use.</summary>
    public static string PerformAgain(string step) => $"Resume the flow run to perform step '{step}' again.";

    /// <summary>
    /// Names the software module's setting <paramref name="setting"/> a step needs and the command that
    /// gives it a value in the project, after which a resume performs the step again.
    /// </summary>
    public static string SetSetting(string step, string project, string setting, string example) =>
        $"Give the software module's setting '{setting}' a value in project '{project}' ({SoftwareSettings.SetCommand(project, setting, example)}), then resume the flow run to perform step '{step}' again.";

    /// <summary>
    /// Names the task's repos field and the command that sets it to the one repo the task works in, after
    /// which a resume performs the step again.
    /// </summary>
    public static string NameRepo(string step, string project, string parcel, IReadOnlyList<string> repos) =>
        $"Set the task's '{SoftwareSettings.ReposField}' field to the one repo it works in, which is one of {string.Join(", ", repos.Select(r => $"'{r}'"))} "
        + $"(coa parcel set {parcel} {SoftwareSettings.ReposField} --item <repo> --project {project}), then resume the flow run to perform step '{step}' again.";

    /// <summary>
    /// Names the way out of a flow run on a parcel whose type version declares no repos field in a project
    /// with more than one repo: nothing can name the repo, so the flow run is stopped.
    /// </summary>
    public static string NoReposField(string step) =>
        $"Stop the flow run; performing step '{step}' again would find no '{SoftwareSettings.ReposField}' field to name the repo by and pause the same way, while a task created under a type version that declares the field can name it.";

    /// <summary>
    /// Names the software module's credential profile setting, whose selected profile yields no GitHub
    /// token, and the command that selects one, after which a resume performs the step again. The profile
    /// selected is the only place a token is read from, so no other account stands in for it.
    /// </summary>
    public static string SelectProfile(string step, string project, string? tokenPath)
    {
        string select = $"one of this node's credential profiles that holds a GitHub token for the software module's setting '{CredentialProfileSetting}' in project '{project}' "
            + $"(coa project module select-profile {project} {SoftwareSteps.Module} <profile>)";
        string fix = tokenPath is null
            ? $"Select {select}"
            : $"Put a GitHub token in {tokenPath}, or select {select}";
        return $"{fix}, then resume the flow run to perform step '{step}' again.";
    }

    /// <summary>The name the credential profile setting goes by wherever a module's settings are listed by name.</summary>
    public const string CredentialProfileSetting = "credential-profile";

    /// <summary>
    /// Names the way out of a flow run whose push someone else's update of the branch on the remote
    /// refused: a resume pushes with the same lease and is refused the same way, while a new flow run's
    /// worktree step fetches the branch and builds on what the remote holds.
    /// </summary>
    public static string RemoteBranchMoved(string step) =>
        $"Stop the flow run and start a new one on the parcel; someone else updated its branch on the remote, so performing step '{step}' again would push with the same lease and be refused the same way, while a new flow run's worktree step fetches the branch and builds on the remote's commits.";

    /// <summary>
    /// Names the way out of a rebase that found the flow run's worktree or branch other than the step
    /// left them: a resume finds them the same way and pauses again.
    /// </summary>
    public static string RebaseRefused(string step) =>
        $"Stop the flow run; performing step '{step}' again would find the branch and its worktree as they are now and pause the same way.";

    /// <summary>
    /// Names the recovery from a rebase onto the primary branch that stopped on a conflict. Resolving
    /// a conflict is a code change, so it goes through build, whose gates and both reviews run on it:
    /// the person rebases the branch in a worktree of their own cut from the node's clone, which is
    /// bare, fetching the primary branch first because the clone's copy of it is only as fresh as its
    /// last fetch, and removes that worktree before moving the task back, because a worktree left
    /// holding the branch at a path that is no flow run's work path pauses the build's worktree step. That
    /// step accepts the rewritten branch while the remote still holds the commit this node last pushed
    /// for the parcel, and the build's open-pr pushes it.
    /// </summary>
    public static string RebaseConflict(string clone, string branch, string primaryBranch) =>
        $"Stop the flow run. Then add a worktree of {branch} from the node's clone at {clone}, at a path outside the daemon's work paths (git --git-dir={clone} worktree add <path> {branch}); "
        + $"in it, fetch {primaryBranch} (git fetch origin {primaryBranch}) and rebase {branch} onto it (git rebase origin/{primaryBranch}), resolving the conflicts, without pushing. "
        + $"Remove that worktree (git --git-dir={clone} worktree remove <path>), and move the task back to build, so the build flow gates, reviews, and pushes the resolved branch and a merge follows.";
}
