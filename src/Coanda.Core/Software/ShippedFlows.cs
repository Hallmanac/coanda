using System.Text.Json;
using Coanda.Core.Flows;

namespace Coanda.Core.Software;

/// <summary>
/// The flows the binary carries: build, merge, discovery, and review on assignment. Each is ordinary
/// <see cref="Flow"/> data with no privileges a user's flow lacks. Every answer and
/// every system step result routes on a string field named <c>verdict</c>. The software module's worktree and gates
/// steps and move-parcel answer <c>done</c> on success, gates answers <c>failed</c> when the build or tests
/// fail, and open-pr answers <c>opened</c> once its pull request is open. The build flow ends
/// there: it opens the pull request, moves the task to review, and ends with the outcome
/// <c>pr-opened</c>, and the merge is a flow of its own that follows. The merge flow holds the
/// gated merge, which answers <c>merged</c>, <c>pending</c>, or <c>behind</c>, with a waiting lap
/// through a timer back to it and a rebase lap (a worktree of the merge flow run's own, a rebase that
/// answers <c>done</c>, then the same timer) for a head behind the primary branch, and closes its
/// task as <c>completed</c> when it ends. A flow run's outcome is the one
/// its flow declares, carried when it reaches end, so no shipped flow holds an emit-outcome step.
/// The build flow's agent steps declare the task's objective, criteria, and non-goals, so each
/// agent it spawns is briefed with them, and its fix step declares the gates and both reviews as
/// sources, so it is handed the latest result and findings that routed it there.
/// </summary>
public static class ShippedFlows
{
    public const string Verdict = "verdict";

    // The software module's steps, by the prefixed names its describe lists. The core cannot reference
    // the module, so the names are written here, and a test holds them to the module's own list.
    private const string Worktree = "software/worktree";
    private const string Gates = "software/gates";
    private const string OpenPr = "software/open-pr";
    private const string GatedMerge = "software/gated-merge";
    private const string Rebase = "software/rebase";

    private const int ReviewToFixCap = 3;
    private const int DefaultCap = 5;

    /// <summary>The seconds each lap of the merge's wait takes.</summary>
    public const int MergeWaitSeconds = 60;

    /// <summary>
    /// How many rebase laps a merge flow run takes: the visit cap on its worktree step's transition to
    /// the rebase, the one step visited once per lap. A head still behind after them pauses on that
    /// cap once the worktree step has run, before the rebase fetches or pushes anything.
    /// </summary>
    public const int MergeRebaseCap = 3;

    /// <summary>
    /// How many visits to the gated merge can answer pending or behind and go round again before it
    /// pauses. Every visit counts, a rebase lap's included, so with <see cref="MergeWaitSeconds"/> the
    /// waiting with the rebase laps taken out is twenty minutes.
    /// </summary>
    public const int MergePendingCap = 20 + MergeRebaseCap;

    /// <summary>
    /// How many resumes of the merge, after its lap has paused, can still end the flow run with a merge.
    /// A visit cap counts every visit a flow run has made to the step, resumes included, so once the lap
    /// is spent each resume looks at the pull request once and pauses again while it is still pending;
    /// this allowance keeps a person who resumes once the checks have finished from ever finding the
    /// merge's own cap spent first.
    /// </summary>
    public const int MergeResumes = 100;

    /// <summary>
    /// The visit cap on the merge's transition to end: <see cref="MergeResumes"/> visits beyond the
    /// pending cap, so a merge made on the lap's last visit, or on any resume after the lap's pause, a
    /// refused close, or another pause, ends the flow run rather than pausing on a visit cap.
    /// </summary>
    public const int MergedCap = MergePendingCap + MergeResumes;

    /// <summary>The task fields the build flow's agent steps carry in their context: what to build, how it is judged, and what is out of scope.</summary>
    private static readonly IReadOnlyList<string> Brief = ["objective", "criteria", "non-goals"];

    /// <summary>The steps whose latest recordings the fix step's context carries: the gates result and both reviews' findings it acts on.</summary>
    private static readonly IReadOnlyList<FlowSource> FixSources =
        [new() { Step = "gates" }, new() { Step = "adversarial-review" }, new() { Step = "conformance-review" }];

    public static readonly Flow Build = new()
    {
        Name = "build",
        Accepts = ["task"],
        Outcome = "pr-opened",
        Budget = 25m,
        SpawnCap = 12,
        Steps =
        [
            SystemStep("worktree", Worktree, On("done", "build")),
            AgentStep("build", "build", "build", On("done", "gates"), On("blocked", Flow.Pause)) with { Fields = Brief },
            SystemStep("gates", Gates, On("done", "adversarial-review"), On("failed", "fix")),
            AgentStep("adversarial-review", "reviewer-adversarial", "review",
                On("clean", "conformance-review"), On("not-clean", "fix", ReviewToFixCap)) with { Fields = Brief },
            AgentStep("conformance-review", "reviewer-conformance", "review",
                On("clean", "open-pr"), On("not-clean", "fix", ReviewToFixCap)) with { Fields = Brief },
            SystemStep("open-pr", OpenPr, On("opened", "move-to-review")),
            new FlowStep
            {
                Name = "move-to-review",
                SystemStep = CoreSystemSteps.MoveParcel,
                Arguments = new Dictionary<string, JsonElement> { ["stage"] = FlowValue.Of("review") },
                Transitions = [On("done", Flow.End)],
            },
            AgentStep("fix", "fix", "fix", On("done", "gates"), On("blocked", Flow.Pause)) with { Fields = Brief, Sources = FixSources },
        ],
    };

    public static readonly Flow Merge = new()
    {
        Name = "merge",
        Accepts = ["task"],
        Outcome = "merged",
        Budget = 1m,
        SpawnCap = 1,
        Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 3 },
        Closes = "completed",
        Steps =
        [
            SystemStep("gated-merge", GatedMerge,
                On("merged", Flow.End, MergedCap),
                On("pending", "wait-for-checks", MergePendingCap),
                On("behind", "worktree", MergePendingCap)),
            new FlowStep
            {
                Name = "wait-for-checks",
                SystemStep = CoreSystemSteps.Timer,
                Arguments = new Dictionary<string, JsonElement> { ["seconds"] = FlowValue.Of(MergeWaitSeconds) },
                Transitions = [On("done", "gated-merge", MergePendingCap)],
            },
            SystemStep("worktree", Worktree, On("done", "rebase", MergeRebaseCap)),
            SystemStep("rebase", Rebase, On("done", "wait-for-checks", MergeRebaseCap)),
        ],
    };

    public static readonly Flow Discovery = new()
    {
        Name = "discovery",
        Accepts = ["idea"],
        Outcome = "parcels-derived",
        Budget = 5m,
        SpawnCap = 3,
        Steps =
        [
            AgentStep("discover", "discovery", "discovery",
                On("derive", "derive-parcels"), On("carry-forward", Flow.End)),
            SystemStep("derive-parcels", CoreSystemSteps.DeriveParcels, On("done", Flow.End)),
        ],
    };

    public static readonly Flow ReviewOnAssignment = new()
    {
        Name = "review-on-assignment",
        Accepts = ["task"],
        Outcome = "report-produced",
        Budget = 3m,
        SpawnCap = 2,
        Steps =
        [
            AgentStep("review", "reviewer-adversarial", "review",
                On("clean", Flow.End), On("not-clean", Flow.End)),
        ],
    };

    public static readonly IReadOnlyList<Flow> All = [Build, Merge, Discovery, ReviewOnAssignment];

    public static Flow? Find(string name) =>
        All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static FlowTransition On(string verdict, string target, int visitCap = DefaultCap) =>
        new() { Field = Verdict, Value = FlowValue.Of(verdict), VisitCap = visitCap, Target = target };

    private static FlowStep AgentStep(string name, string agent, string answerSchema, params FlowTransition[] transitions) =>
        new() { Name = name, Agent = agent, AnswerSchema = answerSchema, Transitions = transitions };

    private static FlowStep SystemStep(string name, string systemStep, params FlowTransition[] transitions) =>
        new() { Name = name, SystemStep = systemStep, Transitions = transitions };
}
