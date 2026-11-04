using System.Globalization;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;

namespace Coanda.Cli.Commands;

/// <summary>
/// The rows <c>coa type list</c> and <c>coa contour list</c> write, as markup with every cell
/// escaped, so a name holding square brackets prints as itself rather than being read as a style.
/// </summary>
internal static class DefinitionRendering
{
    public static readonly string[] TypeListColumns = ["Type", "Hash", "States", "Resolutions"];

    public static readonly string[] ContourListColumns = ["Contour", "Version", "Hash"];

    public static string[] TypeListRow(TypeView type) => Escaped(
    [
        type.Name,
        type.Hash,
        string.Join(", ", type.States),
        string.Join(", ", type.Resolutions),
    ]);

    public static string[] ContourListRow(ContourView contour) => Escaped(
    [
        contour.Name,
        contour.Version.ToString(CultureInfo.InvariantCulture),
        contour.Hash,
    ]);

    private static string[] Escaped(string[] cells) => [.. cells.Select(Markup.Escape)];
}
