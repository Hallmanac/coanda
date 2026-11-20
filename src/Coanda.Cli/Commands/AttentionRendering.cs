using System.Globalization;
using Coanda.Core;
using Coanda.Daemon.Api.Endpoints;

namespace Coanda.Cli.Commands;

/// <summary>
/// What <c>coa attention list</c> asks the daemon and prints, as plain text built from the daemon's views
/// alone, so a test over the endpoint can read exactly what a person would.
/// </summary>
internal static class AttentionRendering
{
    public static readonly string[] Columns = ["Project", "Flow run", "Parcel", "Title", "Flow", "Step", "Reason", "Lever", "Paused"];

    /// <summary>The width the table is written at when the output is not a terminal, wide enough that no row wraps.</summary>
    public const int UnwrappedWidth = 1000;

    /// <summary>The columns holding free text, which wrap in a narrow terminal; every other column keeps to one line.</summary>
    private static readonly string[] Free = ["Title", "Reason", "Lever"];

    /// <summary>Whether <paramref name="column"/> holds free text, and so may wrap.</summary>
    public static bool Wraps(string column) => Free.Contains(column, StringComparer.Ordinal);

    /// <summary>The loopback path the command reads: every project's paused flow runs, or only <paramref name="project"/>'s when it is given.</summary>
    public static string PathFor(string? project) =>
        project is null
            ? "/attention"
            : $"/attention?project={Uri.EscapeDataString(project)}";

    public static string[] Row(AttentionView entry) =>
    [
        entry.Project,
        Ids.Short(entry.FlowRunId),
        Ids.Short(entry.ParcelId),
        entry.ParcelTitle,
        entry.FlowName,
        entry.Step,
        entry.Reason,
        entry.Lever,
        entry.PausedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
    ];

    /// <summary>The sentence printed when nothing is paused, in <paramref name="project"/> when one was named.</summary>
    public static string Nothing(string? project) =>
        project is null
            ? "No flow run this node started is paused."
            : $"No flow run this node started is paused in project '{project.Trim()}'.";
}
