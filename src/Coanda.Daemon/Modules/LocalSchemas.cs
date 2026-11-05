using System.Text.Json;
using Coanda.Core;
using NJsonSchema;
using NJsonSchema.References;

namespace Coanda.Daemon.Modules;

/// <summary>
/// Reads a schema a module supplies, resolving its references inside the schema alone and refusing one
/// to a file or a URL, so reading a module's schema never reads a file of the node, a profile's among
/// them, or reaches the network.
/// </summary>
public static class LocalSchemas
{
    /// <summary>
    /// The schema <paramref name="schema"/> holds. A reference outside it is refused with a
    /// <see cref="DomainException"/> whose rule <paramref name="refused"/> words from the reference, and a
    /// schema that cannot be read with one whose rule <paramref name="unreadable"/> words from the reason.
    /// </summary>
    public static async Task<JsonSchema> ReadAsync(
        string schema, Func<string, string> refused, Func<string, string> unreadable, CancellationToken cancellationToken)
    {
        // Every reference outside the document is refused before the reader sees the text, so none of
        // them, whatever its form, is fetched or read; the resolver below refuses any that slips past.
        if (OutsideReference(schema) is { } outside)
        {
            throw new DomainException(refused(outside));
        }

        try
        {
            return await JsonSchema.FromJsonAsync(
                schema, documentPath: null, read => new LocalReferencesOnly(new JsonSchemaAppender(read, new DefaultTypeNameGenerator()), refused), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The schema is the module's, so a schema the reader cannot read is a rule the module broke.
            throw exception.InnerException as DomainException
                ?? new DomainException(unreadable(exception.Message));
        }
    }

    /// <summary>The first <c>$ref</c> in <paramref name="schema"/> that is not a fragment of the schema itself, or null when there is none or the text is not JSON.</summary>
    private static string? OutsideReference(string schema)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(schema);
            return OutsideReference(document.RootElement);
        }
        catch (JsonException)
        {
            // Text that is not JSON is the reader's to refuse as unreadable.
            return null;
        }
    }

    private static string? OutsideReference(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Select(p => p is { Name: "$ref", Value.ValueKind: JsonValueKind.String } && p.Value.GetString() is { } reference && !reference.StartsWith('#')
                ? reference
                : OutsideReference(p.Value))
            .FirstOrDefault(r => r is not null),
        JsonValueKind.Array => element.EnumerateArray().Select(OutsideReference).FirstOrDefault(r => r is not null),
        _ => null,
    };

    private sealed class LocalReferencesOnly(JsonSchemaAppender appender, Func<string, string> refused) : JsonReferenceResolver(appender)
    {
        public override Task<IJsonReference> ResolveFileReferenceAsync(string filePath, CancellationToken cancellationToken) =>
            throw new DomainException(refused(filePath));

        public override Task<IJsonReference> ResolveUrlReferenceAsync(string url, CancellationToken cancellationToken) =>
            throw new DomainException(refused(url));
    }
}
