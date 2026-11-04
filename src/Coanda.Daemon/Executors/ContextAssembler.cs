using System.Text.RegularExpressions;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Builds the per-parcel context body a step's spawn carries. It reads only the parcel record and
/// the step; it never takes the agent definition, so the persona reaches the executor only through
/// the definition on the spawn and is never folded into the context. Each value is folded onto its
/// own line, so a title holding a line break cannot start a labelled line of its own.
/// </summary>
public static partial class ContextAssembler
{
    public static string Assemble(Parcel parcel, ContourStep step)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(step);

        return string.Join(
            '\n',
            $"Parcel: {parcel.Id}",
            $"Project: {OneLine(parcel.Project)}",
            $"Type: {OneLine(parcel.TypeName)}",
            $"State: {OneLine(parcel.State)}",
            $"Title: {OneLine(parcel.Title)}",
            $"Step: {OneLine(step.Name)}");
    }

    /// <summary>Replaces each run of control characters or line separators with one space.</summary>
    private static string OneLine(string value) => LineBreaking().Replace(value, " ");

    [GeneratedRegex(@"[\p{Cc}  ]+")]
    private static partial Regex LineBreaking();
}
