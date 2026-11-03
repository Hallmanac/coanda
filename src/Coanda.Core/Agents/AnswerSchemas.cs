using System.Text;
using System.Text.Json;

namespace Coanda.Core.Agents;

/// <summary>
/// The catalog of answer schemas the release ships: build, review, fix, and discovery. They are
/// fixed by the release and are not stored as definitions, so customizing one is not possible.
/// Each requires a string field named <c>verdict</c> with a closed set of values and allows
/// further optional fields.
/// </summary>
public static class AnswerSchemas
{
    public const string VerdictField = "verdict";
    public const string Build = "build";
    public const string Review = "review";
    public const string Fix = "fix";
    public const string Discovery = "discovery";

    public static IReadOnlyList<AnswerSchema> All { get; } =
    [
        Create(Build, ["done", "blocked"], withFindings: false),
        Create(Review, ["clean", "not-clean"], withFindings: true),
        Create(Fix, ["done", "blocked"], withFindings: false),
        Create(Discovery, ["derive", "carry-forward"], withFindings: false),
    ];

    public static IReadOnlyList<string> Names { get; } = [.. All.Select(s => s.Name)];

    public static bool Holds(string? name) => Find(name) is not null;

    public static AnswerSchema? Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    public static AnswerSchema Require(string? name) =>
        Find(name) ?? throw new DomainException(
            $"'{name}' is not a shipped answer schema; the catalog holds {string.Join(", ", Names)}.");

    private static AnswerSchema Create(string name, string[] verdicts, bool withFindings)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", "http://json-schema.org/draft-07/schema#");
            writer.WriteString("title", $"The answer of a {name} step");
            writer.WriteString("type", "object");
            writer.WritePropertyName("required");
            writer.WriteStartArray();
            writer.WriteStringValue(VerdictField);
            writer.WriteEndArray();
            writer.WritePropertyName("properties");
            writer.WriteStartObject();

            writer.WritePropertyName(VerdictField);
            writer.WriteStartObject();
            writer.WriteString("type", "string");
            writer.WritePropertyName("enum");
            writer.WriteStartArray();
            foreach (string verdict in verdicts)
            {
                writer.WriteStringValue(verdict);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("summary");
            writer.WriteStartObject();
            writer.WriteString("type", "string");
            writer.WriteEndObject();

            if (withFindings)
            {
                writer.WritePropertyName("findings");
                writer.WriteStartObject();
                writer.WriteString("type", "array");
                writer.WritePropertyName("items");
                writer.WriteStartObject();
                writer.WriteString("type", "object");
                writer.WritePropertyName("required");
                writer.WriteStartArray();
                writer.WriteStringValue("title");
                writer.WriteEndArray();
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                foreach (string field in new[] { "title", "detail", "location" })
                {
                    writer.WritePropertyName(field);
                    writer.WriteStartObject();
                    writer.WriteString("type", "string");
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new AnswerSchema(name, verdicts, Encoding.UTF8.GetString(stream.ToArray()));
    }
}
