using System.Text.Json;
using Coanda.Sdk.Client;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// Reads the commit the module last pushed for a parcel from what its flow runs recorded, through the
/// core's API: the <c>head</c> field of the most recently recorded result of a step whose system step
/// is open-pr or rebase. A step is matched by the system step it names, never by its name, because a
/// project's own flows may name steps differently and the worktree step's result carries a
/// <c>head</c> of its own; a flow run that started under a flow naming the step without the module's
/// prefix recorded it under that name, which <see cref="SoftwareSteps.Canonical"/> reads as the module's.
/// </summary>
public static class RecordedHeads
{
    /// <summary>
    /// The head pushed last among the flow runs of the dispatch's own chain, or null when none of
    /// them recorded a push or the latest carries no head. The merge's head guard and the rebase
    /// read this one.
    /// </summary>
    public static async Task<string?> OfChainAsync(StepContext context, CancellationToken cancellationToken)
    {
        CoreFlowRun flowRun = await context.FlowRunAsync(cancellationToken);
        IReadOnlyList<CoreFlowRun> onParcel = await context.Core.FlowRunsAsync(context.Request.Project, flowRun.ParcelId, cancellationToken);
        return Latest(onParcel.Where(f => f.RootFlowRunId == flowRun.RootFlowRunId));
    }

    /// <summary>
    /// The head pushed last among all the parcel's flow runs, whichever chain they belong to, or null
    /// when none of them recorded a push or the latest carries no head. A move back to build starts
    /// a fresh chain, so the worktree step reads every chain on the parcel.
    /// </summary>
    public static async Task<string?> OfParcelAsync(StepContext context, CancellationToken cancellationToken) =>
        Latest(await context.Core.FlowRunsAsync(context.Request.Project, context.Request.ParcelId, cancellationToken));

    private static string? Latest(IEnumerable<CoreFlowRun> flowRuns)
    {
        CoreFlowRunStep? latest = flowRuns
            .SelectMany(f => f.Steps)
            .Where(entry => entry is { Kind: "system-step", Result: not null, RecordedAt: not null }
                && SoftwareSteps.Canonical(entry.Performer) is SoftwareSteps.OpenPr or SoftwareSteps.Rebase)
            .MaxBy(entry => entry.RecordedAt);
        return latest?.Result is { ValueKind: JsonValueKind.Object } result
            && result.TryGetProperty("head", out JsonElement head)
            && head.ValueKind == JsonValueKind.String
            && head.GetString() is { Length: > 0 } sha
                ? sha
                : null;
    }
}
