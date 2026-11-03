using System.Text.Json;
using Coanda.Core.Flows;

namespace Coanda.Core.Software;

/// <summary>
/// The flows the binary carries: build, discovery, and review on assignment. Each is ordinary
/// <see cref="FlowDefinition"/> data with no privileges a user's flow lacks. Every answer and
/// every action result routes on a string field named <c>verdict</c>; an action answers
/// <c>done</c> on success, and gates may also answer <c>failed</c>. A flight's outcome is the one
/// its flow declares, carried when it reaches end, so no shipped flow holds an emit-outcome step.
/// </summary>
public static class ShippedFlows
{
    public const string Verdict = "verdict";

    private const int ReviewToFixCap = 3;
    private const int DefaultCap = 5;

    public static readonly FlowDefinition Build = new()
    {
        Name = "build",
        Accepts = ["task"],
        Outcome = "pr-opened",
        Budget = 25m,
        SpawnCap = 12,
        Steps =
        [
            Action("worktree", SoftwareActions.Worktree, On("done", "build")),
            Judgment("build", "build", "build", On("done", "gates"), On("blocked", FlowDefinition.Park)),
            Action("gates", SoftwareActions.Gates, On("done", "adversarial-review"), On("failed", "fix")),
            Judgment("adversarial-review", "reviewer-adversarial", "review",
                On("clean", "conformance-review"), On("not-clean", "fix", ReviewToFixCap)),
            Judgment("conformance-review", "reviewer-conformance", "review",
                On("clean", "open-pr"), On("not-clean", "fix", ReviewToFixCap)),
            Action("open-pr", SoftwareActions.OpenPr, On("done", "gated-merge")),
            Action("gated-merge", SoftwareActions.GatedMerge, On("done", "move-to-review")),
            new FlowStep
            {
                Name = "move-to-review",
                Action = CoreActions.MoveParcel,
                Arguments = new Dictionary<string, JsonElement> { ["state"] = FlowValue.Of("review") },
                Transitions = [On("done", FlowDefinition.End)],
            },
            Judgment("fix", "fix", "fix", On("done", "gates"), On("blocked", FlowDefinition.Park)),
        ],
    };

    public static readonly FlowDefinition Discovery = new()
    {
        Name = "discovery",
        Accepts = ["idea"],
        Outcome = "parcels-derived",
        Budget = 5m,
        SpawnCap = 3,
        Steps =
        [
            Judgment("discover", "discovery", "discovery",
                On("derive", "derive-parcels"), On("carry-forward", FlowDefinition.End)),
            Action("derive-parcels", CoreActions.DeriveParcels, On("done", FlowDefinition.End)),
        ],
    };

    public static readonly FlowDefinition ReviewOnAssignment = new()
    {
        Name = "review-on-assignment",
        Accepts = ["task"],
        Outcome = "report-produced",
        Budget = 3m,
        SpawnCap = 2,
        Steps =
        [
            Judgment("review", "reviewer-adversarial", "review",
                On("clean", FlowDefinition.End), On("not-clean", FlowDefinition.End)),
        ],
    };

    public static readonly IReadOnlyList<FlowDefinition> All = [Build, Discovery, ReviewOnAssignment];

    public static FlowDefinition? Find(string name) =>
        All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static FlowTransition On(string verdict, string target, int visitCap = DefaultCap) =>
        new() { Field = Verdict, Value = FlowValue.Of(verdict), VisitCap = visitCap, Target = target };

    private static FlowStep Judgment(string name, string agent, string answerSchema, params FlowTransition[] transitions) =>
        new() { Name = name, Agent = agent, AnswerSchema = answerSchema, Transitions = transitions };

    private static FlowStep Action(string name, string action, params FlowTransition[] transitions) =>
        new() { Name = name, Action = action, Transitions = transitions };
}
