namespace Coanda.Core.Projects.Events;

/// <summary>
/// The set of modules switched on for the project was named, replacing the set it held; an empty set
/// switches every module off. Each is a module's name (lowercase letters and digits in runs joined by
/// single hyphens), recorded once, in ordinal order. It is recorded in the project's own tenant, so it
/// replicates with the project.
/// </summary>
public sealed record ProjectModulesSet(
    Guid ProjectId,
    string Project,
    IReadOnlyList<string> Modules,
    string SetBy,
    DateTimeOffset SetAt);
