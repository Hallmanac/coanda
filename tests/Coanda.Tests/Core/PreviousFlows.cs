using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Modules.Software;
using Fisher;

namespace Coanda.Tests.Core;

/// <summary>
/// Shipped flows as an earlier build carried them, for tests of what a store seeded by that build holds.
/// Every build before the software module was carved out named its five steps without the module's
/// prefix, so these do too.
/// </summary>
public static class PreviousFlows
{
    /// <summary>
    /// The system step names a store seeded by this build or an earlier one validated its flows against:
    /// the core's, the software module's, and the software steps' names before the prefix.
    /// </summary>
    public static IReadOnlySet<string> SystemSteps { get; } =
        new HashSet<string>([.. CoreSystemSteps.All, .. SoftwareSteps.All, .. SoftwareSteps.Unprefixed], StringComparer.Ordinal);

    /// <summary>The build flow as the last build before the prefix shipped it: <see cref="BuildBeforeReviewDiff"/>, with the software steps unprefixed.</summary>
    public static Flow Build => Unprefixed(BuildBeforeReviewDiff);

    /// <summary>
    /// The build flow as main shipped it before its review diff step: today's, with that step taken out
    /// and gates' done transition going straight on to adversarial-review, the step the review diff
    /// step's own done transition targets.
    /// </summary>
    public static Flow BuildBeforeReviewDiff
    {
        get
        {
            Flow build = SoftwareFlows.Build;
            FlowStep reviewDiff = build.Step("review-diff") ?? throw new InvalidOperationException("The build flow has no review-diff step.");
            string onward = reviewDiff.Transitions.Single().Target;
            return build with
            {
                Steps =
                [
                    .. build.Steps.Where(s => s.Name != reviewDiff.Name).Select(s => s with
                    {
                        Transitions = [.. s.Transitions.Select(t => t.Target == reviewDiff.Name ? t with { Target = onward } : t)],
                    }),
                ],
            };
        }
    }

    /// <summary>
    /// The build flow before it ended at the open pull request: open-pr answered done into a
    /// gated-merge step, which went on to move the task to review.
    /// </summary>
    public static Flow BuildWithGatedMerge
    {
        get
        {
            Flow build = Build;
            FlowStep openPr = build.Step("open-pr") ?? throw new InvalidOperationException("The build flow has no open-pr step.");
            FlowTransition onward = openPr.Transitions.Single();
            FlowStep gatedMerge = new()
            {
                Name = "gated-merge",
                SystemStep = "gated-merge",
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
                SystemStep = "gated-merge",
                Transitions = [On("merged", Flow.End, 120), On("pending", "wait-for-checks", 20)],
            },
            new FlowStep
            {
                Name = "wait-for-checks",
                SystemStep = CoreSystemSteps.Timer,
                Arguments = new Dictionary<string, JsonElement> { ["seconds"] = FlowValue.Of(60) },
                Transitions = [On("done", "gated-merge", 20)],
            },
        ],
    };

    /// <summary>
    /// Drafts and publishes <paramref name="flow"/> as a project-scope flow of <paramref name="project"/>
    /// in the store, recording the same events the build that shipped it recorded, which knew the
    /// unprefixed software steps as system steps.
    /// </summary>
    public static async Task PublishAsync(IDocumentStore store, string project, Flow flow)
    {
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: SystemSteps);
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, project, flow.Name,
            Flow.CanonicalJson(flow), flow.Hash, null, CancellationToken.None);
        await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, CancellationToken.None);
    }

    /// <summary><paramref name="flow"/> with each of the software module's steps named as builds before the prefix named it.</summary>
    public static Flow Unprefixed(Flow flow) => flow with
    {
        Steps =
        [
            .. flow.Steps.Select(s => s.SystemStep is { } step && SoftwareSteps.All.Contains(step)
                ? s with { SystemStep = step[(SoftwareSteps.Module.Length + 1)..] }
                : s),
        ],
    };

    private static FlowTransition On(string verdict, string target, int cap) =>
        new() { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = cap, Target = target };
}
