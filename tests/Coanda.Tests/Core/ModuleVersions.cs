using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;

namespace Coanda.Tests.Core;

/// <summary>
/// Module-scope definitions for a store-free test: a definition a module brought, published, at
/// whichever version number the store would have given it.
/// </summary>
public static class ModuleVersions
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <paramref name="definition"/> as the published module-scope definition the store numbered
    /// <paramref name="version"/>, imported from <paramref name="module"/> at <paramref name="moduleVersion"/>.
    /// </summary>
    public static Definition Of(ModuleDefinition definition, int version = 1, string module = "software", string moduleVersion = "0.1.0")
    {
        var drafted = new DefinitionDrafted(
            Ids.New(), definition.Kind, definition.Name, DefinitionScope.Module, null, version,
            definition.Content, definition.ContentHash, null, "coanda-import", At, module, moduleVersion);
        var imported = Definition.Create(drafted);
        imported.Apply(new DefinitionPublished(drafted.DefinitionId, "coanda-import", At));
        return imported;
    }
}
