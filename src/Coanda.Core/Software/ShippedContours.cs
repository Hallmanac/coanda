using System.Text.Json;
using Coanda.Core.Contours;

namespace Coanda.Core.Software;

/// <summary>
/// The contours the binary carries: build, merge, discovery, and review on assignment. Each is ordinary
/// <see cref="Contour"/> data with no privileges a user's contour lacks. Every answer and
/// every action result routes on a string field named <c>verdict</c>. The worktree and gates actions
/// and move-parcel answer <c>done</c> on success, gates answers <c>failed</c> when the build or tests
/// fail, and open-pr answers <c>opened</c> once its pull request is open. The build contour ends
/// there: it opens the pull request, moves the task to review, and ends with the outcome
/// <c>pr-opened</c>, and the merge is a contour of its own that follows. The merge contour holds the
/// gated merge, which answers <c>merged</c> or <c>pending</c>, with a waiting lap through a timer
/// back to it, and closes its task as <c>completed</c> when it ends. A flow's outcome is the one
/// its contour declares, carried when it reaches end, so no shipped contour holds an emit-outcome step.
/// The build contour's judgments declare the task's objective, criteria, and non-goals, so each
/// agent it spawns is briefed with them, and its fix step declares the gates and both reviews as
/// sources, so it is handed the latest result and findings that routed it there.
/// </summary>
public static class ShippedContours
{
    public const string Verdict = "verdict";

    private const int ReviewToFixCap = 3;
    private const int DefaultCap = 5;

    /// <summary>The seconds each lap of the merge's wait takes.</summary>
    public const int MergeWaitSeconds = 60;

    /// <summary>
    /// How many times the merge answers pending and waits again before it pauses: with
    /// <see cref="MergeWaitSeconds"/>, twenty minutes of waiting in all.
    /// </summary>
    public const int MergePendingCap = 20;

    /// <summary>
    /// How many resumes of the merge, after its lap has paused, can still end the flow with a merge.
    /// A visit cap counts every visit a flow has made to the step, resumes included, so once the lap
    /// is spent each resume looks at the pull request once and pauses again while it is still pending;
    /// this allowance keeps a person who resumes once the checks have finished from ever finding the
    /// merge's own cap spent first.
    /// </summary>
    public const int MergeResumes = 100;

    /// <summary>
    /// The visit cap on the merge's transition to end: <see cref="MergeResumes"/> visits beyond the
    /// pending cap, so a merge made on the lap's last visit, or on any resume after the lap's pause, a
    /// refused close, or another pause, ends the flow rather than pausing on a visit cap.
    /// </summary>
    public const int MergedCap = MergePendingCap + MergeResumes;

    /// <summary>The task fields the build contour's judgments carry in their context: what to build, how it is judged, and what is out of scope.</summary>
    private static readonly IReadOnlyList<string> Brief = ["objective", "criteria", "non-goals"];

    /// <summary>The steps whose latest recordings the fix step's context carries: the gates result and both reviews' findings it acts on.</summary>
    private static readonly IReadOnlyList<ContourSource> FixSources =
        [new() { Step = "gates" }, new() { Step = "adversarial-review" }, new() { Step = "conformance-review" }];

    public static readonly Contour Build = new()
    {
        Name = "build",
        Accepts = ["task"],
        Outcome = "pr-opened",
        Budget = 25m,
        SpawnCap = 12,
        Steps =
        [
            Action("worktree", SoftwareActions.Worktree, On("done", "build")),
            Judgment("build", "build", "build", On("done", "gates"), On("blocked", Contour.Pause)) with { Fields = Brief },
            Action("gates", SoftwareActions.Gates, On("done", "adversarial-review"), On("failed", "fix")),
            Judgment("adversarial-review", "reviewer-adversarial", "review",
                On("clean", "conformance-review"), On("not-clean", "fix", ReviewToFixCap)) with { Fields = Brief },
            Judgment("conformance-review", "reviewer-conformance", "review",
                On("clean", "open-pr"), On("not-clean", "fix", ReviewToFixCap)) with { Fields = Brief },
            Action("open-pr", SoftwareActions.OpenPr, On("opened", "move-to-review")),
            new ContourStep
            {
                Name = "move-to-review",
                Action = CoreActions.MoveParcel,
                Arguments = new Dictionary<string, JsonElement> { ["state"] = ContourValue.Of("review") },
                Transitions = [On("done", Contour.End)],
            },
            Judgment("fix", "fix", "fix", On("done", "gates"), On("blocked", Contour.Pause)) with { Fields = Brief, Sources = FixSources },
        ],
    };

    public static readonly Contour Merge = new()
    {
        Name = "merge",
        Accepts = ["task"],
        Outcome = "merged",
        Budget = 1m,
        SpawnCap = 1,
        Follow = new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 3 },
        Closes = "completed",
        Steps =
        [
            Action("gated-merge", SoftwareActions.GatedMerge,
                On("merged", Contour.End, MergedCap), On("pending", "wait-for-checks", MergePendingCap)),
            new ContourStep
            {
                Name = "wait-for-checks",
                Action = CoreActions.Timer,
                Arguments = new Dictionary<string, JsonElement> { ["seconds"] = ContourValue.Of(MergeWaitSeconds) },
                Transitions = [On("done", "gated-merge", MergePendingCap)],
            },
        ],
    };

    public static readonly Contour Discovery = new()
    {
        Name = "discovery",
        Accepts = ["idea"],
        Outcome = "parcels-derived",
        Budget = 5m,
        SpawnCap = 3,
        Steps =
        [
            Judgment("discover", "discovery", "discovery",
                On("derive", "derive-parcels"), On("carry-forward", Contour.End)),
            Action("derive-parcels", CoreActions.DeriveParcels, On("done", Contour.End)),
        ],
    };

    public static readonly Contour ReviewOnAssignment = new()
    {
        Name = "review-on-assignment",
        Accepts = ["task"],
        Outcome = "report-produced",
        Budget = 3m,
        SpawnCap = 2,
        Steps =
        [
            Judgment("review", "reviewer-adversarial", "review",
                On("clean", Contour.End), On("not-clean", Contour.End)),
        ],
    };

    public static readonly IReadOnlyList<Contour> All = [Build, Merge, Discovery, ReviewOnAssignment];

    public static Contour? Find(string name) =>
        All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ContourTransition On(string verdict, string target, int visitCap = DefaultCap) =>
        new() { Field = Verdict, Value = ContourValue.Of(verdict), VisitCap = visitCap, Target = target };

    private static ContourStep Judgment(string name, string agent, string answerSchema, params ContourTransition[] transitions) =>
        new() { Name = name, Agent = agent, AnswerSchema = answerSchema, Transitions = transitions };

    private static ContourStep Action(string name, string action, params ContourTransition[] transitions) =>
        new() { Name = name, Action = action, Transitions = transitions };
}
