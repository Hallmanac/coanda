namespace Coanda.Core.Definitions;

/// <summary>
/// Which definition governs a project's work. Nearest scope wins by name, in the order project,
/// owner, shipped; within a scope the highest published version wins. A draft or a retired
/// version is never returned, and a project-scope definition is returned only for its own project.
/// </summary>
public static class DefinitionResolution
{
    private static readonly DefinitionScope[] NearestFirst = [DefinitionScope.Project, DefinitionScope.Owner, DefinitionScope.Shipped];

    public static Definition? Resolve(IEnumerable<Definition> candidates, string project, DefinitionKind kind, string name)
    {
        Definition[] governing = [.. Governing(candidates, project, kind).Where(d => SameName(d.Name, name))];
        return NearestFirst
            .Select(scope => governing.Where(d => d.Scope == scope).MaxBy(d => d.VersionNumber))
            .FirstOrDefault(found => found is not null);
    }

    /// <summary>The resolved definition of every name the project can see, ordered by name.</summary>
    public static IReadOnlyList<Definition> ResolveAll(IEnumerable<Definition> candidates, string project, DefinitionKind kind)
    {
        Definition[] governing = [.. Governing(candidates, project, kind)];
        return
        [
            .. governing
                .Select(d => d.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => Resolve(governing, project, kind, name))
                .OfType<Definition>()
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    public static bool SameName(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Definition> Governing(IEnumerable<Definition> candidates, string project, DefinitionKind kind) =>
        candidates.Where(d => d.Kind == kind
            && d.Status == DefinitionStatus.Published
            && (d.Scope != DefinitionScope.Project || string.Equals(d.Project, project, StringComparison.Ordinal)));
}
