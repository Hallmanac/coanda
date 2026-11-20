using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Executors;

/// <summary>
/// What a step's sources of the newer kinds read from this node's record, gathered in the step's
/// project before <see cref="ContextAssembler.Assemble"/> renders them, so the rendering stays a pure
/// function of what was read. Each member holds only what a source the step declares needs; a step
/// whose sources name only steps reads <see cref="None"/>.
/// </summary>
/// <param name="FlowRuns">Every flow run on the step's parcel, the current one included, in the order <c>FlowRunService.ListAsync</c> returns them.</param>
/// <param name="Parcels">The project's parcels, read when a type source's type resolves.</param>
/// <param name="ResolvedTypes">The type names the step's type sources name that resolve for the project, compared without regard to case.</param>
/// <param name="OriginParcel">The parcel the step's parcel's derived-from relation names, or null when it is not in this node's record or not read.</param>
/// <param name="OriginFlowRun">The flow run that relation names, or null when it names none, it is not in this node's record, or it was not read.</param>
public sealed record SourceReads(
    IReadOnlyList<FlowRun> FlowRuns,
    IReadOnlyList<Parcel> Parcels,
    IReadOnlySet<string> ResolvedTypes,
    Parcel? OriginParcel,
    FlowRun? OriginFlowRun)
{
    /// <summary>Nothing read: what a step whose sources name only steps needs.</summary>
    public static SourceReads None { get; } = new([], [], new HashSet<string>(StringComparer.OrdinalIgnoreCase), null, null);
}
