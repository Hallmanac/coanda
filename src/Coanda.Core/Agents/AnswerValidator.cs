using System.Collections.Concurrent;
using System.Text.Json;
using NJsonSchema;
using NJsonSchema.References;
using NJsonSchema.Validation;

namespace Coanda.Core.Agents;

/// <summary>
/// Checks an agent's answer against the agent's answer schema. A document that is not JSON is
/// reported as invalid, not thrown. A schema is read with a reference resolver that cannot leave
/// the document: a reference to a file or a URL is refused rather than followed, so nothing about a
/// schema reads a file or makes a network request, even for content stored without the check a
/// draft makes.
/// </summary>
public static class AnswerValidator
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Compiled = new(StringComparer.Ordinal);

    public static AnswerValidation Validate(AnswerSchema schema, string document)
    {
        ArgumentNullException.ThrowIfNull(schema);
        string text = document ?? string.Empty;

        // System.Text.Json is strict about what JSON is (the validator's own reader accepts
        // comments, single quotes, and bare names), so it decides first.
        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            return AnswerValidation.Invalid($"The answer is not a JSON document: {exception.Message}");
        }

        ICollection<ValidationError> failures = Read(schema.Document).Validate(text);
        return failures.Count == 0
            ? AnswerValidation.Valid
            : new AnswerValidation([.. failures.SelectMany(Describe)]);
    }

    /// <summary>
    /// Reads a JSON Schema document as the validator does, resolving only references within it, and
    /// keeps what it read for the next time the same text is read. Throws whatever the reader throws
    /// for a document it refuses, and keeps nothing then.
    /// </summary>
    internal static JsonSchema Read(string document) => Compiled.GetOrAdd(document, Compile);

    private static JsonSchema Compile(string document) =>
        // With no document path the reader resolves no relative or file reference, and the resolver
        // refuses a URL or a file in any case, so this reads nothing but the text in memory and
        // completes synchronously; there is nothing to cancel.
        JsonSchema.FromJsonAsync(
                document,
                documentPath: null,
                schema => new SelfContainedResolver(new JsonSchemaAppender(schema, new DefaultTypeNameGenerator())),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    // An error inside an array item or a subschema arrives as a ChildSchemaValidationError whose
    // children hold the actual cause. Each child's path is already the full path from the root, so
    // the enclosing line is kept and every child is listed after it, recursively.
    private static IEnumerable<string> Describe(ValidationError error) => error switch
    {
        ChildSchemaValidationError child =>
            [Line(error), .. child.Errors.Values.SelectMany(errors => errors).SelectMany(Describe)],
        _ => [Line(error)],
    };

    private static string Line(ValidationError error) => $"{error.Kind} at {error.Path ?? "#"}";

    /// <summary>The default resolver with its two ways out of the document closed.</summary>
    private sealed class SelfContainedResolver(JsonSchemaAppender appender) : JsonReferenceResolver(appender)
    {
        public override Task<IJsonReference> ResolveFileReferenceAsync(string filePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                $"The answer schema refers to '{filePath}', outside itself; an answer schema resolves only references within its own content.");

        public override Task<IJsonReference> ResolveUrlReferenceAsync(string url, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                $"The answer schema refers to '{url}', outside itself; an answer schema resolves only references within its own content.");
    }
}
