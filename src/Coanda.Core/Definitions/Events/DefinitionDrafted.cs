namespace Coanda.Core.Definitions.Events;

/// <summary>
/// A definition's version recorded as a draft. <paramref name="Module"/> and
/// <paramref name="ModuleVersion"/> name the module and version a module-scope definition was imported
/// from, and are null in any other scope; neither is part of the content hash. A record written before
/// module scope carries neither (see <see cref="EarlierShippedRecords"/>).
/// </summary>
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
    DateTimeOffset DraftedAt,
    string? Module = null,
    string? ModuleVersion = null);
