namespace Coanda.Core.Definitions.Events;

public sealed record DefinitionDrafted(
    Guid DefinitionId,
    DefinitionKind Kind,
    string Name,
    DefinitionScope Scope,
    string? Project,
    int Version,
    string Content,
    string ContentHash,
    ForkBase? ForkBase,
    string DraftedBy,
    DateTimeOffset DraftedAt);
