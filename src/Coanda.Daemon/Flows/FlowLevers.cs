namespace Coanda.Daemon.Flows;

/// <summary>The lever a pause carries when nothing more specific names what a person acts on.</summary>
public static class FlowLevers
{
    /// <summary>Names the step a resume performs again.</summary>
    public static string PerformAgain(string step) => $"Resume the flow to perform step '{step}' again.";

    /// <summary>Names the way out of a flow that has spawned as many agents as its contour allows: a resume cannot spawn another.</summary>
    public static string SpawnCapReached(string contour) =>
        $"Stop the flow; contour '{contour}' allows no more spawns, so a resume cannot perform another judgment.";

    /// <summary>Names the way out of a flow whose current step no transition's visit cap allows another visit to: a resume pauses it again.</summary>
    public static string VisitCapsSpent(string step) =>
        $"Stop the flow; every transition out of step '{step}' has spent its visit cap, so a resume pauses it again.";

    /// <summary>Names the way out of a flow deeper in its chain than its contour's follow allows: a resume pauses it again.</summary>
    public static string DepthCapReached(string contour) =>
        $"Stop the flow; it is past the depth cap of contour '{contour}''s follow, so a resume pauses it again.";

    /// <summary>
    /// Names the way out of a flow whose push someone else's update of the branch on the remote
    /// refused: a resume pushes with the same lease and is refused the same way, while a new flow's
    /// worktree step fetches the branch and builds on what the remote holds.
    /// </summary>
    public static string RemoteBranchMoved(string step) =>
        $"Stop the flow and start a new one on the parcel; someone else updated its branch on the remote, so performing step '{step}' again would push with the same lease and be refused the same way, while a new flow's worktree step fetches the branch and builds on the remote's commits.";

    /// <summary>Names the way out of a flow whose budget, or its chain's, is spent: a resume pauses it again.</summary>
    public static string BudgetReached() =>
        "Stop the flow; its budget is spent, so a resume pauses it again before any judgment.";

    /// <summary>
    /// Names the one recovery from a pull request whose head is behind the primary branch that the
    /// head guard accepts: the rebase is never pushed by hand, so the build contour gates, reviews, and
    /// pushes the rebased branch, and open-pr records the head a new merge follows.
    /// </summary>
    public static string RebaseThroughBuild(string branch, string primaryBranch) =>
        $"Stop the flow, rebase {branch} onto {primaryBranch} in the node's clone without pushing it, and move the task back to build, so the build contour gates, reviews, and pushes the rebased branch and a merge follows again.";
}
