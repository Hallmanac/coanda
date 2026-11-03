namespace Coanda.Core.Definitions.Events;

public sealed record DefinitionRetired(
    Guid DefinitionId,
    string RetiredBy,
    DateTimeOffset RetiredAt);
