using System.Text.Json;
using Coanda.Core.Contours;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Reads a step's arguments as the JSON kind an action expects. An argument is read, never
/// evaluated; one that is missing or of the wrong kind is refused with
/// <see cref="ActionArgumentException"/>, which <see cref="FlowActions"/> turns into a pause.
/// </summary>
public static class ActionArguments
{
    public static string RequireString(ContourStep step, string argument)
    {
        JsonElement value = Require(step, argument, "a string");
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return string.IsNullOrWhiteSpace(text)
            ? throw new ActionArgumentException(step.Name, argument, "a string that is not blank")
            : text;
    }

    public static double RequireNonNegativeNumber(ContourStep step, string argument)
    {
        JsonElement value = Require(step, argument, "a number");
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) && number >= 0
            ? number
            : throw new ActionArgumentException(step.Name, argument, "a number of zero or more");
    }

    private static JsonElement Require(ContourStep step, string argument, string expected) =>
        step.Arguments.TryGetValue(argument, out JsonElement value)
            ? value
            : throw new ActionArgumentException(step.Name, argument, expected);
}

/// <summary>A step's argument is missing or of the wrong JSON kind for its action.</summary>
public sealed class ActionArgumentException(string step, string argument, string expected)
    : Exception($"Step '{step}' needs its argument '{argument}' as {expected}; the contour gives none of that kind.")
{
    public string Step { get; } = step;

    public string Argument { get; } = argument;
}
