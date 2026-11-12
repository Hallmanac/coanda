using System.Text.Json;

namespace Coanda.Core.Agents;

/// <summary>
/// The rules an agent's answer schema content keeps, checked when the agent is drafted or imported.
/// The content is a JSON object, naming no member twice in one object, that the validator's JSON
/// Schema reader accepts; it resolves no reference outside itself (every <c>$ref</c> is a JSON
/// pointer into the same content, beginning with <c>#</c>), declares <c>"type": "object"</c> at its
/// top level, and requires a <c>verdict</c> declared as <c>"type": "string"</c> with an <c>enum</c>
/// of distinct, non-blank strings, which are the verdicts the agent's answers route on.
/// </summary>
public static class AnswerSchemaRules
{
    public const string VerdictField = "verdict";

    private const string Reference = "$ref";

    /// <summary>
    /// The verdicts <paramref name="content"/> allows, in the order its <c>enum</c> lists them, once
    /// it keeps every rule; a rule it breaks is refused with a message naming the agent and the rule.
    /// </summary>
    public static IReadOnlyList<string> Verdicts(string agent, JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            throw Refused(agent, $"is {Describe(content.ValueKind)}, not a JSON object");
        }

        if (NamedTwice(content) is { } twiceNamed)
        {
            throw Refused(agent, $"names the member '{twiceNamed}' twice in one object");
        }

        if (OutsideReference(content) is { } outside)
        {
            throw Refused(
                agent,
                $"holds the $ref {outside}, which is not a JSON pointer into the same content (one that begins with '#'); an answer schema resolves no reference outside itself");
        }

        try
        {
            AnswerValidator.Read(content.GetRawText());
        }
        catch (Exception refusal) when (refusal is not OutOfMemoryException)
        {
            throw Refused(agent, $"is refused by the JSON Schema reader: {refusal.Message}");
        }

        if (!IsString(content, "type", "object"))
        {
            throw Refused(agent, "does not declare \"type\": \"object\" at its top level");
        }

        if (!content.TryGetProperty("required", out JsonElement required)
            || required.ValueKind != JsonValueKind.Array
            || !required.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == VerdictField))
        {
            throw Refused(agent, $"does not list '{VerdictField}' in its required array");
        }

        if (!content.TryGetProperty("properties", out JsonElement properties)
            || properties.ValueKind != JsonValueKind.Object
            || !properties.TryGetProperty(VerdictField, out JsonElement verdict)
            || verdict.ValueKind != JsonValueKind.Object
            || !IsString(verdict, "type", "string"))
        {
            throw Refused(agent, $"does not declare '{VerdictField}' as \"type\": \"string\" among its properties");
        }

        if (!verdict.TryGetProperty("enum", out JsonElement values)
            || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() == 0)
        {
            throw Refused(agent, $"does not give '{VerdictField}' an enum that is a non-empty array of verdicts");
        }

        if (values.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString())))
        {
            throw Refused(agent, $"lists a verdict in the enum of '{VerdictField}' that is not a non-blank string");
        }

        string[] verdicts = [.. values.EnumerateArray().Select(v => v.GetString() ?? string.Empty)];
        string? twice = verdicts.GroupBy(v => v, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
        return twice is null
            ? verdicts
            : throw Refused(agent, $"lists the verdict '{twice}' twice in the enum of '{VerdictField}'");
    }

    /// <summary>The first <c>$ref</c> anywhere in the content that is not a string beginning with <c>#</c>, as JSON.</summary>
    private static string? OutsideReference(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Select(member => member.NameEquals(Reference) && !IsInside(member.Value)
                ? member.Value.GetRawText()
                : OutsideReference(member.Value))
            .FirstOrDefault(found => found is not null),
        JsonValueKind.Array => element.EnumerateArray().Select(OutsideReference).FirstOrDefault(found => found is not null),
        _ => null,
    };

    /// <summary>The first member name anywhere in the content that one object holds twice.</summary>
    private static string? NamedTwice(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
                .GroupBy(member => member.Name, StringComparer.Ordinal)
                .FirstOrDefault(named => named.Count() > 1)?.Key
            ?? element.EnumerateObject().Select(member => NamedTwice(member.Value)).FirstOrDefault(found => found is not null),
        JsonValueKind.Array => element.EnumerateArray().Select(NamedTwice).FirstOrDefault(found => found is not null),
        _ => null,
    };

    private static bool IsInside(JsonElement reference) =>
        reference.ValueKind == JsonValueKind.String && reference.GetString() is { } text && text.StartsWith('#');

    private static bool IsString(JsonElement element, string member, string value) =>
        element.TryGetProperty(member, out JsonElement found)
        && found.ValueKind == JsonValueKind.String
        && found.GetString() == value;

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    private static DomainException Refused(string agent, string rule) =>
        new($"Agent '{agent}' holds answer schema content that {rule}.");
}
