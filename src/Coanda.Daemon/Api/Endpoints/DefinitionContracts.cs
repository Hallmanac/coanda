using Coanda.Core.Definitions;
using Coanda.Daemon.Definitions;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>
/// A draft of the definition <see cref="Content"/> holds, a file in the shape a module ships for its
/// <see cref="Kind"/> (<c>parcel-type</c>, <c>flow</c>, <c>agent</c>, or <c>trigger</c>), in
/// <see cref="Scope"/> (<c>project</c> when it is left out, or <c>owner</c>). <see cref="Project"/> is
/// read in project scope only. <see cref="Fork"/> asks for the draft to fork the module-scope
/// definition of its kind and name this node holds. The hash is the daemon's to compute, never the caller's.
/// </summary>
public sealed record DefinitionDraftRequest(string? Project, string Kind, string Content, string? Scope = null, bool Fork = false);

/// <summary>Names one of the definitions that can govern <see cref="Project"/> by its full id.</summary>
public sealed record DefinitionIdRequest(string Project, Guid DefinitionId);

/// <summary>
/// One definition, as <c>coa definition list</c> shows it: its id, kind, name, scope (with the module
/// and module version a module-scope one came with), version, status, content hash, and, for a fork,
/// the module-scope definition it was forked from.
/// </summary>
public sealed record DefinitionView(
    Guid Id,
    string Kind,
    string Name,
    string Scope,
    string? Project,
    string? Module,
    string? ModuleVersion,
    int Version,
    string Status,
    string Hash,
    ForkBase? ForkBase)
{
    public static DefinitionView From(Definition definition) => new(
        definition.Id,
        DefinitionWords.Of(definition.Kind),
        definition.Name,
        DefinitionWords.Of(definition.Scope),
        definition.Project,
        definition.Module,
        definition.ModuleVersion,
        definition.VersionNumber,
        DefinitionWords.Of(definition.Status),
        definition.ContentHash,
        definition.ForkBase);
}

/// <summary>One definition's row and its content, the canonical JSON its hash is the hash of.</summary>
public sealed record DefinitionDetailView(DefinitionView Definition, string Content)
{
    public static DefinitionDetailView From(Definition definition) => new(DefinitionView.From(definition), definition.Content);
}
