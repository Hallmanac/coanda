using System.Globalization;
using System.Text.Json;
using Coanda.Core;
using Coanda.Daemon.Api.Endpoints;

namespace Coanda.Cli.Commands;

/// <summary>
/// What <c>coa flow-run list</c> and <c>coa flow-run show</c> print, as plain text built from the
/// daemon's views alone, so a test over the endpoints can read exactly what a person would.
/// </summary>
internal static class FlowRunRendering
{
    public static readonly string[] ListColumns = ["Id", "Flow", "Status", "Step", "Started"];

    public static string[] ListRow(FlowRunView flowRun) =>
    [
        Ids.Short(flowRun.Id),
        $"{flowRun.FlowName} v{flowRun.FlowVersion}",
        flowRun.Status,
        flowRun.CurrentStep,
        flowRun.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
    ];

    public static IReadOnlyList<string> Show(FlowRunView flowRun)
    {
        List<string> lines =
        [
            Pair("Flow run", flowRun.Id.ToString()),
            Pair("Flow", $"{flowRun.FlowName} v{flowRun.FlowVersion} ({flowRun.FlowHash})"),
            Pair("Parcel", flowRun.ParcelId.ToString()),
            Pair("Status", flowRun.Status),
        ];

        if (flowRun.ParentFlowRunId is Guid parent)
        {
            lines.Add(Pair("Parent", parent.ToString()));
            lines.Add(Pair("Depth", flowRun.Depth.ToString(CultureInfo.InvariantCulture)));
        }

        switch (flowRun.Status)
        {
            case "paused":
                lines.Add(Pair("Reason", flowRun.PauseReason ?? string.Empty));
                lines.Add(Pair("Lever", flowRun.Lever ?? string.Empty));
                break;
            case "ended":
                lines.Add(Pair("Outcome", flowRun.Outcome ?? string.Empty));
                break;
            case "stopped":
                lines.Add(Pair("Reason", flowRun.StopReason ?? string.Empty));
                break;
        }

        lines.Add("Steps");
        foreach (FlowRunStepView step in flowRun.Steps)
        {
            lines.Add(Step(step));
            lines.AddRange(Entries(step));
        }

        return lines;
    }

    /// <summary>The step, the agent or system step it names, and, once recorded, what came back.</summary>
    public static string Step(FlowRunStepView step)
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

    /// <summary>
    /// One line under the step for each entry of each top-level list in what it recorded, numbered from one,
    /// so a list the step line leaves out, such as the tasks a discovery answer would derive, can be read
    /// before a person answers the gate that acts on it. An object entry prints its fields as name=value.
    /// </summary>
    public static IEnumerable<string> Entries(FlowRunStepView step) =>
        (step.Answer ?? step.Result) is { ValueKind: JsonValueKind.Object } document
            ? document.EnumerateObject()
                .Where(field => field.Value.ValueKind == JsonValueKind.Array)
                .SelectMany(field => field.Value.EnumerateArray().Select((entry, index) =>
                    "    " + string.Join("  ", [$"{field.Name}[{index + 1}]", .. Entry(entry)])))
            : [];

    /// <summary>The top-level string, number, and boolean fields of a document, as name=value.</summary>
    public static IEnumerable<string> Fields(JsonElement document) =>
        document.ValueKind != JsonValueKind.Object
            ? []
            : document.EnumerateObject()
                .Where(field => field.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .Select(field => $"{field.Name}={(field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText())}");

    private static IEnumerable<string> Entry(JsonElement entry) => entry.ValueKind switch
    {
        JsonValueKind.Object => Fields(entry),
        JsonValueKind.String => [entry.GetString() ?? string.Empty],
        _ => [entry.GetRawText()],
    };

    private static string Pair(string key, string value) => $"{key,-8}{value}";
}
