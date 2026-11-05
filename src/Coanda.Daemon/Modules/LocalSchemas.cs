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

    private sealed class LocalReferencesOnly(JsonSchemaAppender appender, Func<string, string> refused) : JsonReferenceResolver(appender)
    {
        public override Task<IJsonReference> ResolveFileReferenceAsync(string filePath, CancellationToken cancellationToken) =>
            throw new DomainException(refused(filePath));

        public override Task<IJsonReference> ResolveUrlReferenceAsync(string url, CancellationToken cancellationToken) =>
            throw new DomainException(refused(url));
    }
}
