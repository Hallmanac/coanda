namespace Coanda.Daemon.FlowRuns;

/// <summary>The lever a pause carries when nothing more specific names what a person acts on.</summary>
public static class FlowRunLevers
{
    /// <summary>Names the step a resume performs again.</summary>
    public static string PerformAgain(string step) => $"Resume the flow run to perform step '{step}' again.";

    /// <summary>Names the way out of a flow run that has spawned as many agents as its contour allows: a resume cannot spawn another.</summary>
    public static string SpawnCapReached(string contour) =>
        $"Stop the flow run; contour '{contour}' allows no more spawns, so a resume cannot perform another judgment.";

    /// <summary>Names the way out of a flow run whose current step no transition's visit cap allows another visit to: a resume pauses it again.</summary>
    public static string VisitCapsSpent(string step) =>
        $"Stop the flow run; every transition out of step '{step}' has spent its visit cap, so a resume pauses it again.";

    /// <summary>Names the way out of a flow run deeper in its chain than its contour's follow allows: a resume pauses it again.</summary>
    public static string DepthCapReached(string contour) =>
        $"Stop the flow run; it is past the depth cap of contour '{contour}''s follow, so a resume pauses it again.";

    /// <summary>
    /// Names the way out of a flow run whose push someone else's update of the branch on the remote
    /// refused: a resume pushes with the same lease and is refused the same way, while a new flow run's
    /// worktree step fetches the branch and builds on what the remote holds.
    /// </summary>
    public static string RemoteBranchMoved(string step) =>
        $"Stop the flow run and start a new one on the parcel; someone else updated its branch on the remote, so performing step '{step}' again would push with the same lease and be refused the same way, while a new flow run's worktree step fetches the branch and builds on the remote's commits.";

    /// <summary>Names the way out of a flow run whose budget, or its chain's, is spent: a resume pauses it again.</summary>
    public static string BudgetReached() =>
        "Stop the flow run; its budget is spent, so a resume pauses it again before any judgment.";

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
    /// step accepts the rewritten branch while the remote still holds the commit last pushed, and the
    /// build's open-pr pushes it.
    /// </summary>
    public static string RebaseConflict(string clone, string branch, string primaryBranch) =>
        $"Stop the flow run. Then add a worktree of {branch} from the node's clone at {clone}, at a path outside the daemon's work paths (git --git-dir={clone} worktree add <path> {branch}); "
        + $"in it, fetch {primaryBranch} (git fetch origin {primaryBranch}) and rebase {branch} onto it (git rebase origin/{primaryBranch}), resolving the conflicts, without pushing. "
        + $"Remove that worktree (git --git-dir={clone} worktree remove <path>), and move the task back to build, so the build contour gates, reviews, and pushes the resolved branch and a merge follows.";
}
