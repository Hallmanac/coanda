using System.Globalization;
using System.Text.Json;
using Coanda.Core;
using Coanda.Daemon.Api;

namespace Coanda.Cli.Commands;

/// <summary>
/// What <c>coa flight list</c> and <c>coa flight show</c> print, as plain text built from the
/// daemon's views alone, so a test over the endpoints can read exactly what a person would.
/// </summary>
internal static class FlightRendering
{
    public static readonly string[] ListColumns = ["Id", "Flow", "Status", "Step", "Started"];

    public static string[] ListRow(FlightView flight) =>
    [
        Ids.Short(flight.Id),
        $"{flight.Flow} v{flight.FlowVersion}",
        flight.Status,
        flight.CurrentStep,
        flight.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
    ];

    public static IReadOnlyList<string> Show(FlightView flight)
    {
        List<string> lines =
        [
            Pair("Flight", flight.Id.ToString()),
            Pair("Flow", $"{flight.Flow} v{flight.FlowVersion} ({flight.FlowHash})"),
            Pair("Parcel", flight.ParcelId.ToString()),
            Pair("Status", flight.Status),
        ];

        switch (flight.Status)
        {
            case "parked":
                lines.Add(Pair("Reason", flight.ParkReason ?? string.Empty));
                lines.Add(Pair("Lever", flight.Lever ?? string.Empty));
                break;
            case "ended":
                lines.Add(Pair("Outcome", flight.Outcome ?? string.Empty));
                break;
            case "stopped":
                lines.Add(Pair("Reason", flight.StopReason ?? string.Empty));
                break;
        }

        lines.Add("Steps");
        lines.AddRange(flight.Steps.Select(Step));
        return lines;
    }

    /// <summary>The step, the agent or action it names, and, once recorded, what came back.</summary>
    public static string Step(FlightStepView step)
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
