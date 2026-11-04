using System.Text.Json;
using Coanda.Core.Flows;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Reads a step's arguments as the JSON kind a system step expects. An argument is read, never
/// evaluated; one that is missing or of the wrong kind is refused with
/// <see cref="SystemStepArgumentException"/>, which <see cref="FlowSystemSteps"/> turns into a pause.
/// </summary>
public static class SystemStepArguments
{
    public static string RequireString(FlowStep step, string argument)
    {
        JsonElement value = Require(step, argument, "a string");
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(text)
            ? throw new SystemStepArgumentException(step.Name, argument, "a string that is not blank")
            : text;
    }

    public static double RequireNonNegativeNumber(FlowStep step, string argument)
    {
        JsonElement value = Require(step, argument, "a number");
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) && number >= 0
            ? number
            : throw new SystemStepArgumentException(step.Name, argument, "a number of zero or more");
    }

    private static JsonElement Require(FlowStep step, string argument, string expected) =>
        step.Arguments.TryGetValue(argument, out JsonElement value)
            ? value
            : throw new SystemStepArgumentException(step.Name, argument, expected);
}

/// <summary>A step's argument is missing or of the wrong JSON kind for its system step.</summary>
public sealed class SystemStepArgumentException(string step, string argument, string expected)
    : Exception($"Step '{step}' needs its argument '{argument}' as {expected}; the flow gives none of that kind.")
{
    public string Step { get; } = step;

    public string Argument { get; } = argument;
}
