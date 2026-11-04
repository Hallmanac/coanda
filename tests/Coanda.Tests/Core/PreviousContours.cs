using System.Text.Json;
using Coanda.Core.Contours;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

/// <summary>Shipped contours as an earlier build carried them, for tests of what a store seeded by that build holds.</summary>
public static class PreviousContours
{
    /// <summary>
    /// The build contour before it ended at the open pull request: open-pr answered done into a
    /// gated-merge step, which went on to move the task to review.
    /// </summary>
    public static Contour BuildWithGatedMerge
    {
        get
        {
            Contour build = ShippedContours.Build;
            ContourStep openPr = build.Step("open-pr") ?? throw new InvalidOperationException("The build contour has no open-pr step.");
            ContourTransition onward = openPr.Transitions.Single();
            ContourStep gatedMerge = new()
            {
                Name = "gated-merge",
                Action = SoftwareActions.GatedMerge,
                Transitions = [onward with { Value = ContourValue.Of("done") }],
            };
            return build with
            {
                Steps =
                [
                    .. build.Steps.SelectMany<ContourStep, ContourStep>(s => s.Name == "open-pr"
                        ? [s with { Transitions = [onward with { Value = ContourValue.Of("done"), Target = "gated-merge" }] }, gatedMerge]
                        : [s]),
                ],
            };
        }
    }

    /// <summary>
    /// The merge contour before its rebase lap, written out as that build shipped it rather than
    /// derived from the current one: gated-merge answered merged or pending, and pending waited a
    /// minute and looked again, twenty times.
    /// </summary>
    public static Contour MergeWithoutRebase { get; } = new()
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
            new ContourStep
            {
                Name = "gated-merge",
                Action = SoftwareActions.GatedMerge,
                Transitions = [On("merged", Contour.End, 120), On("pending", "wait-for-checks", 20)],
            },
            new ContourStep
            {
                Name = "wait-for-checks",
                Action = CoreActions.Timer,
                Arguments = new Dictionary<string, JsonElement> { ["seconds"] = ContourValue.Of(60) },
                Transitions = [On("done", "gated-merge", 20)],
            },
        ],
    };

    private static ContourTransition On(string verdict, string target, int cap) =>
        new() { Field = "verdict", Value = ContourValue.Of(verdict), VisitCap = cap, Target = target };
}
