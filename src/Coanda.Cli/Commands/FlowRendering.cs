using System.Globalization;
using System.Text.Json;
using Coanda.Core;
using Coanda.Daemon.Api.Endpoints;

namespace Coanda.Cli.Commands;

/// <summary>
/// What <c>coa flow list</c> and <c>coa flow show</c> print, as plain text built from the
/// daemon's views alone, so a test over the endpoints can read exactly what a person would.
/// </summary>
internal static class FlowRendering
{
    public static readonly string[] ListColumns = ["Id", "Contour", "Status", "Step", "Started"];

    public static string[] ListRow(FlowView flow) =>
    [
        Ids.Short(flow.Id),
        $"{flow.ContourName} v{flow.ContourVersion}",
        flow.Status,
        flow.CurrentStep,
        flow.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
    ];

    public static IReadOnlyList<string> Show(FlowView flow)
    {
        List<string> lines =
        [
            Pair("Flow", flow.Id.ToString()),
            Pair("Contour", $"{flow.ContourName} v{flow.ContourVersion} ({flow.ContourHash})"),
            Pair("Parcel", flow.ParcelId.ToString()),
            Pair("Status", flow.Status),
        ];

        if (flow.ParentFlowId is Guid parent)
        {
            lines.Add(Pair("Parent", parent.ToString()));
            lines.Add(Pair("Depth", flow.Depth.ToString(CultureInfo.InvariantCulture)));
        }

        switch (flow.Status)
        {
            case "paused":
                lines.Add(Pair("Reason", flow.PauseReason ?? string.Empty));
                lines.Add(Pair("Lever", flow.Lever ?? string.Empty));
                break;
            case "ended":
                lines.Add(Pair("Outcome", flow.Outcome ?? string.Empty));
                break;
            case "stopped":
                lines.Add(Pair("Reason", flow.StopReason ?? string.Empty));
                break;
        }

        lines.Add("Steps");
        lines.AddRange(flow.Steps.Select(Step));
        return lines;
    }

    /// <summary>The step, the agent or action it names, and, once recorded, what came back.</summary>
    public static string Step(FlowStepView step)
    {
        List<string> parts = [step.Step, $"{step.Kind} {step.Performer}"];
        JsonElement? recorded = step.Answer ?? step.Result;
        if (recorded is JsonElement document)
        {
            parts.AddRange(Fields(document));
        }

        if (step.Measurement is { } measurement)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"tokens {measurement.Tokens}, cost ${measurement.CostUsd:0.00##}, {measurement.Duration.TotalSeconds:0.#}s"));
        }

        return "  " + string.Join("  ", parts);
    }

    /// <summary>The top-level string, number, and boolean fields of a document, as name=value.</summary>
    public static IEnumerable<string> Fields(JsonElement document) =>
        document.ValueKind != JsonValueKind.Object
            ? []
            : document.EnumerateObject()
                .Where(field => field.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .Select(field => $"{field.Name}={(field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText())}");

    private static string Pair(string key, string value) => $"{key,-8}{value}";
}
