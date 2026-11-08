namespace Coanda.Core.Projects.Events;

/// <summary>
/// A project was registered on the node: its name and its repos, each a name and a git remote, the
/// remote recorded exactly as given.
/// </summary>
public sealed record ProjectRegistered(
    Guid ProjectId,
    string Name,
    IReadOnlyList<ProjectRepo>? Repos,
    string RegisteredBy,
    DateTimeOffset RegisteredAt);
