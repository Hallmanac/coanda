using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Software;
using Coanda.Daemon.Flows;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Reads the commit coanda last pushed for a parcel from what its flows recorded: the <c>head</c>
/// field of the most recently recorded result of a step whose action is open-pr or rebase. A step
/// is matched by the action it names, never by its name, because a project's own contours may name
/// steps differently and the worktree step's result carries a <c>head</c> of its own.
/// </summary>
public static class RecordedHeads
{
    /// <summary>
    /// The head pushed last among the flows of the requesting flow's chain, or null when none of
    /// them recorded a push or the latest carries no head. The merge's head guard and the rebase
    /// read this one.
    /// </summary>
    public static async Task<string?> OfChainAsync(FlowService flows, ActionRequest request, CancellationToken cancellationToken)
    {
        Flow flow = await flows.ShowAsync(request.Principal, request.Project, request.FlowId, cancellationToken)
            ?? throw new DomainException($"No flow {Ids.Short(request.FlowId)} in project '{request.Project}'.");
        return Latest(await flows.ChainOfAsync(request.Principal, request.Project, flow, cancellationToken));
    }

    /// <summary>
    /// The head pushed last among all the parcel's flows, whichever chain they belong to, or null
    /// when none of them recorded a push or the latest carries no head. A move back to build starts
    /// a fresh chain, so the worktree step reads every chain on the parcel.
    /// </summary>
    public static async Task<string?> OfParcelAsync(FlowService flows, ActionRequest request, CancellationToken cancellationToken) =>
        Latest(await flows.ListAsync(request.Principal, request.Project, request.ParcelId, cancellationToken));

    private static string? Latest(IEnumerable<Flow> flows)
    {
        FlowStepEntry? latest = flows
            .SelectMany(f => f.Steps)
            .Where(entry => entry is { Kind: StepKind.Action, Performer: SoftwareActions.OpenPr or SoftwareActions.Rebase, Result: not null, RecordedAt: not null })
            .MaxBy(entry => entry.RecordedAt);
        return latest?.Result is { ValueKind: JsonValueKind.Object } result
            && result.TryGetProperty("head", out JsonElement head)
            && head.ValueKind == JsonValueKind.String
            && head.GetString() is { Length: > 0 } sha
                ? sha
                : null;
    }
}
