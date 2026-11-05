using System.Text.Json;
using Coanda.Sdk.Client;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// Reads the head a parcel's flow runs recorded as pushed, through the core's API, which need not be a
/// push this node made, since the store may hold flow runs another node recorded: the <c>head</c>
/// field of the most recently recorded result of a step whose system step is open-pr or rebase. A step is matched by the system step it names, never by its name, because a
/// project's own flows may name steps differently and the worktree step's result carries a
/// <c>head</c> of its own; a flow run that started under a flow naming the step without the module's
/// prefix recorded it under that name, which <see cref="SoftwareSteps.Canonical"/> reads as the module's.
/// </summary>
public static class RecordedHeads
{
    /// <summary>
    /// The head recorded as pushed last among the flow runs of the dispatch's own chain, or null when
    /// none of them recorded a push or the latest carries no head. The merge's head guard and the
    /// rebase read this one; both use it only to guard, as the head a pull request must hold and the
    /// lease a push expects, so a head this node did not push ends in a pause, never an overwrite.
    /// </summary>
    public static async Task<string?> OfChainAsync(StepContext context, CancellationToken cancellationToken)
    {
        CoreFlowRun flowRun = await context.FlowRunAsync(cancellationToken);
        IReadOnlyList<CoreFlowRun> onParcel = await context.Core.FlowRunsAsync(context.Request.Project, flowRun.ParcelId, cancellationToken);
        return Latest(onParcel.Where(f => f.RootFlowRunId == flowRun.RootFlowRunId));
    }

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
