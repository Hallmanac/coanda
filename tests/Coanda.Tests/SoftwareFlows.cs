using Coanda.Core.Flows;

namespace Coanda.Tests;

/// <summary>
/// The software module's flows, build, merge, discovery, and review on assignment, read from its
/// definitions folder, with the merge flow's caps as that flow's transitions declare them.
/// </summary>
public static class SoftwareFlows
{
    public static readonly Flow Build = Flow.FromJson(SoftwareDefinitions.Read("flows", "build"));

    public static readonly Flow Merge = Flow.FromJson(SoftwareDefinitions.Read("flows", "merge"));

    public static readonly Flow Discovery = Flow.FromJson(SoftwareDefinitions.Read("flows", "discovery"));

    public static readonly Flow ReviewOnAssignment = Flow.FromJson(SoftwareDefinitions.Read("flows", "review-on-assignment"));

    public static readonly IReadOnlyList<Flow> All = [Build, Merge, Discovery, ReviewOnAssignment];

    /// <summary>The visit cap on the gated merge's pending lap: how many visits to it can answer pending or behind and go round again.</summary>
    public static int MergePendingCap => MergeCap("pending");

    /// <summary>How many visits beyond the pending cap the gated merge's merged transition allows, so a resume can still end the flow run with a merge.</summary>
    public static int MergeResumes => MergeCap("merged") - MergePendingCap;

    public static Flow? Find(string name) =>
        All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static int MergeCap(string verdict) =>
        (Merge.Step("gated-merge") ?? throw new InvalidOperationException("The merge flow has no gated-merge step."))
            .Transitions.Single(t => t.Value.GetString() == verdict).VisitCap;
}
