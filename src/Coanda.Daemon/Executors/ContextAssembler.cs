using Coanda.Core.Flows;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Builds the per-parcel context body a step's spawn carries. It reads only the parcel record and
/// the step; it never takes the agent definition, so the persona reaches the executor only through
/// the definition on the spawn and is never folded into the context.
/// </summary>
public static class ContextAssembler
{
    public static string Assemble(Parcel parcel, FlowStep step)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(step);

        return string.Join(
            '\n',
            $"Parcel: {parcel.Id}",
            $"Project: {parcel.Project}",
            $"Type: {parcel.TypeName}",
            $"State: {parcel.State}",
            $"Title: {parcel.Title}",
            $"Step: {step.Name}");
    }
}
