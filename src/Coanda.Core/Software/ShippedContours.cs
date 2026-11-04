using System.Text.Json;
using Coanda.Core.Contours;

namespace Coanda.Core.Software;

/// <summary>
/// The contours the binary carries: build, discovery, and review on assignment. Each is ordinary
/// <see cref="Contour"/> data with no privileges a user's contour lacks. Every answer and
/// every action result routes on a string field named <c>verdict</c>; an action answers
/// <c>done</c> on success, and gates may also answer <c>failed</c>. A flow's outcome is the one
/// its contour declares, carried when it reaches end, so no shipped contour holds an emit-outcome step.
/// </summary>
public static class ShippedContours
{
    public const string Verdict = "verdict";

    private const int ReviewToFixCap = 3;
    private const int DefaultCap = 5;

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
            Judgment("build", "build", "build", On("done", "gates"), On("blocked", Contour.Pause)),
            Action("gates", SoftwareActions.Gates, On("done", "adversarial-review"), On("failed", "fix")),
            Judgment("adversarial-review", "reviewer-adversarial", "review",
                On("clean", "conformance-review"), On("not-clean", "fix", ReviewToFixCap)),
            Judgment("conformance-review", "reviewer-conformance", "review",
                On("clean", "open-pr"), On("not-clean", "fix", ReviewToFixCap)),
            Action("open-pr", SoftwareActions.OpenPr, On("done", "gated-merge")),
            Action("gated-merge", SoftwareActions.GatedMerge, On("done", "move-to-review")),
            new ContourStep
            {
                Name = "move-to-review",
                Action = CoreActions.MoveParcel,
                Arguments = new Dictionary<string, JsonElement> { ["state"] = ContourValue.Of("review") },
                Transitions = [On("done", Contour.End)],
            },
            Judgment("fix", "fix", "fix", On("done", "gates"), On("blocked", Contour.Pause)),
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

    public static readonly IReadOnlyList<Contour> All = [Build, Discovery, ReviewOnAssignment];

    public static Contour? Find(string name) =>
        All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static ContourTransition On(string verdict, string target, int visitCap = DefaultCap) =>
        new() { Field = Verdict, Value = ContourValue.Of(verdict), VisitCap = visitCap, Target = target };

    private static ContourStep Judgment(string name, string agent, string answerSchema, params ContourTransition[] transitions) =>
        new() { Name = name, Agent = agent, AnswerSchema = answerSchema, Transitions = transitions };

    private static ContourStep Action(string name, string action, params ContourTransition[] transitions) =>
        new() { Name = name, Action = action, Transitions = transitions };
}
