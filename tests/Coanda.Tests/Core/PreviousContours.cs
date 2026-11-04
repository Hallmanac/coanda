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
}
