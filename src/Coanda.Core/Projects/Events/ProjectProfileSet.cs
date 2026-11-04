namespace Coanda.Core.Projects.Events;

/// <summary>
/// A project was set to spawn its agent steps with the named credential profile of this node. It is
/// recorded in the node tenant, never in the project's own, so nothing the project replicates
/// carries it. A later one for the same project replaces the name.
/// </summary>
public sealed record ProjectProfileSet(
    Guid SettingId,
    string Project,
    string Profile,
    string SetBy,
    DateTimeOffset SetAt);
