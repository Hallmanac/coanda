using System.Text.Json;

namespace Coanda.Core.Contours;

/// <summary>
/// The only values a contour holds: a JSON string, number, or boolean. A transition's value and an
/// action's arguments are these, compared for equality and never evaluated.
/// </summary>
public static class ContourValue
{
    public static JsonElement Of(string value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Of(double value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Of(bool value) => JsonSerializer.SerializeToElement(value);

    public static bool IsAllowed(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;
}
