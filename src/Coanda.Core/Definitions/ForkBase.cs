namespace Coanda.Core.Definitions;

/// <summary>
/// The module-scope definition a customization was forked from, by values every node agrees on: its
/// <paramref name="Name"/>, the <paramref name="Module"/> that brought it, the
/// <paramref name="ModuleVersion"/> the drafting node holds it under, and its
/// <paramref name="ContentHash"/>. The name, module, and content hash identify the base on any node,
/// since a name is held by one module and import never records a content twice; the module version
/// is recorded for the reader but not matched, because one module version can hold more than one
/// content of a definition, and a node records an unchanged content under the first module version
/// it imported it at.
/// </summary>
public sealed record ForkBase(string Name, string? Module, string? ModuleVersion, string? ContentHash)
{
    /// <summary>A fork base naming <paramref name="moduleDefinition"/>, a module-scope definition, by its name, module, module version, and content hash.</summary>
    public static ForkBase Of(Definition moduleDefinition) =>
        moduleDefinition.Scope == DefinitionScope.Module
            ? new ForkBase(moduleDefinition.Name, moduleDefinition.Module, moduleDefinition.ModuleVersion, moduleDefinition.ContentHash)
            : throw new ArgumentException(
                $"A fork base names a module-scope definition; '{moduleDefinition.Name}' is {moduleDefinition.Scope} scope.", nameof(moduleDefinition));

    /// <summary>
    /// Whether <paramref name="definition"/> is the module-scope definition this fork base names on the
    /// node holding it, by name, module, and content hash. The kind is the caller's to match.
    /// </summary>
    public bool IsBaseOf(Definition definition) =>
        definition.Scope == DefinitionScope.Module
        && DefinitionResolution.SameName(definition.Name, Name.Trim())
        && string.Equals(definition.Module, Module, StringComparison.Ordinal)
        && string.Equals(definition.ContentHash, ContentHash, StringComparison.Ordinal);

    /// <summary>How a refusal names this fork base.</summary>
    public string Describe() => $"'{Name}' of module '{Module}' version {ModuleVersion} with content hash '{ContentHash}'";
}
