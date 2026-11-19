using System.Globalization;
using Coanda.Core;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;

namespace Coanda.Cli.Commands;

/// <summary>
/// The rows <c>coa type list</c>, <c>coa flow list</c>, and <c>coa definition</c> write, as markup with every cell
/// escaped, so a name holding square brackets prints as itself rather than being read as a style.
/// </summary>
internal static class DefinitionRendering
{
    public static readonly string[] TypeListColumns = ["Type", "Hash", "Stages", "Resolutions"];

    public static readonly string[] FlowListColumns = ["Flow", "Version", "Hash"];

    public static string[] TypeListRow(TypeView type) => Escaped(
    [
        type.Name,
        type.Hash,
        string.Join(", ", type.Stages),
        string.Join(", ", type.Resolutions),
    ]);

    public static string[] FlowListRow(FlowView flow) => Escaped(
    [
        flow.Name,
        flow.Version.ToString(CultureInfo.InvariantCulture),
        flow.Hash,
    ]);

    public static readonly string[] DefinitionListColumns = ["Id", "Kind", "Name", "Scope", "Version", "Status", "Hash"];

    public static string[] DefinitionListRow(DefinitionView definition) => Escaped(
    [
        Ids.Short(definition.Id),
        definition.Kind,
        definition.Name,
        ScopeOf(definition),
        definition.Version.ToString(CultureInfo.InvariantCulture),
        definition.Status,
        definition.Hash,
    ]);

    /// <summary>The key and value rows <c>coa definition show</c>, <c>draft</c>, <c>publish</c>, and <c>retire</c> print for one definition.</summary>
    public static IReadOnlyList<string[]> DefinitionRows(DefinitionView definition)
    {
        List<string[]> rows =
        [
            ["Id", definition.Id.ToString()],
            ["Kind", definition.Kind],
            ["Name", definition.Name],
            ["Scope", ScopeOf(definition)],
            ["Version", definition.Version.ToString(CultureInfo.InvariantCulture)],
            ["Status", definition.Status],
            ["Hash", definition.Hash],
        ];
        if (definition.ForkBase is { } forkBase)
        {
            rows.Add(["Fork of", $"{forkBase.Name} of module {forkBase.Module} {forkBase.ModuleVersion}, hash {forkBase.ContentHash}"]);
        }

        return [.. rows.Select(Escaped)];
    }

    /// <summary>A definition's scope, naming its project in project scope and the module and module version it came with in module scope.</summary>
    public static string ScopeOf(DefinitionView definition) => definition switch
    {
        { Module: { } module } => $"{definition.Scope} ({module} {definition.ModuleVersion})",
        { Project: { } project } => $"{definition.Scope} ({project})",
        _ => definition.Scope,
    };

    private static string[] Escaped(string[] cells) => [.. cells.Select(Markup.Escape)];
}
