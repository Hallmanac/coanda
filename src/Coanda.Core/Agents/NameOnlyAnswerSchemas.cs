using System.Text;
using System.Text.Json;

namespace Coanda.Core.Agents;

/// <summary>
/// The four answer schemas the release shipped by name before an agent carried its own: build,
/// review, fix, and discovery. This map serves one thing only: agent content stored before an agent
/// carried its schema content, which names one of these four and holds no content of its own. Such
/// content is current, since a definition's content and content hash are never changed, and its
/// answers validate against the same document as when it was stored (<see cref="AgentDefinition.ResolveAnswerSchema"/>).
/// Nothing decides whether an agent is valid, or which schema an agent carrying its own content
/// answers in, by looking a name up here. The documents are frozen: changing one changes how stored
/// agents' answers validate.
/// </summary>
public static class NameOnlyAnswerSchemas
{
    private const string VerdictField = AnswerSchemaRules.VerdictField;
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

    public static AnswerSchema? Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

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
