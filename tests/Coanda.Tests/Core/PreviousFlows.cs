using System.Text.Json;
using Coanda.Core.Flows;
using Coanda.Core.Software;

namespace Coanda.Tests.Core;

/// <summary>Shipped flows as an earlier build carried them, for tests of what a store seeded by that build holds.</summary>
public static class PreviousFlows
{
    /// <summary>
    /// The build flow before it ended at the open pull request: open-pr answered done into a
    /// gated-merge step, which went on to move the task to review.
    /// </summary>
    public static Flow BuildWithGatedMerge
    {
        get
        {
            Flow build = ShippedFlows.Build;
            FlowStep openPr = build.Step("open-pr") ?? throw new InvalidOperationException("The build flow has no open-pr step.");
            FlowTransition onward = openPr.Transitions.Single();
            FlowStep gatedMerge = new()
            {
                Name = "gated-merge",
                Action = SoftwareActions.GatedMerge,
                Transitions = [onward with { Value = FlowValue.Of("done") }],
            };
            return build with
            {
                Steps =
                [
                    .. build.Steps.SelectMany<FlowStep, FlowStep>(s => s.Name == "open-pr"
                        ? [s with { Transitions = [onward with { Value = FlowValue.Of("done"), Target = "gated-merge" }] }, gatedMerge]
                        : [s]),
                ],
            };
        }
    }

    /// <summary>
    /// The merge flow before its rebase lap, written out as that build shipped it rather than
    /// derived from the current one: gated-merge answered merged or pending, and pending waited a
    /// minute and looked again, twenty times.
    /// </summary>
    public static Flow MergeWithoutRebase { get; } = new()
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
            new FlowStep
            {
                Name = "gated-merge",
                Action = SoftwareActions.GatedMerge,
                Transitions = [On("merged", Flow.End, 120), On("pending", "wait-for-checks", 20)],
            },
            new FlowStep
            {
                Name = "wait-for-checks",
                Action = CoreActions.Timer,
                Arguments = new Dictionary<string, JsonElement> { ["seconds"] = FlowValue.Of(60) },
                Transitions = [On("done", "gated-merge", 20)],
            },
        ],
    };

    private static FlowTransition On(string verdict, string target, int cap) =>
        new() { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = cap, Target = target };
}
