namespace Coanda.Core.Definitions.Events;

public sealed record DefinitionPublished(
    Guid DefinitionId,
    string PublishedBy,
    DateTimeOffset PublishedAt);
