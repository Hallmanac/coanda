using System.Text.Json;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Core.Agents;

/// <summary>
/// Checks an agent's answer against one of the shipped answer schemas. A document that is not
/// JSON is reported as invalid, not thrown; an unknown schema name is a rule broken.
/// </summary>
public static class AnswerValidator
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonSchema>> Compiled = new(() =>
        AnswerSchemas.All.ToDictionary(s => s.Name, Compile, StringComparer.Ordinal));

    public static AnswerValidation Validate(string schemaName, string document)
    {
        AnswerSchema schema = AnswerSchemas.Require(schemaName);
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

        ICollection<ValidationError> failures = Compiled.Value[schema.Name].Validate(text);
        return failures.Count == 0
            ? AnswerValidation.Valid
            : new AnswerValidation([.. failures.SelectMany(Describe)]);
    }

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

    private static JsonSchema Compile(AnswerSchema schema) =>
        // The schema text is in memory, so this completes synchronously; there is nothing to cancel.
        JsonSchema.FromJsonAsync(schema.Document).GetAwaiter().GetResult();
}
