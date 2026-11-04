namespace Coanda.Core.Projects.Events;

/// <summary>
/// A project was registered on the node: its name, the remote of its one repository, and that
/// repository's primary branch. The remote and the branch are recorded exactly as given.
/// </summary>
public sealed record ProjectRegistered(
    Guid ProjectId,
    string Name,
    string Remote,
    string PrimaryBranch,
    string RegisteredBy,
    DateTimeOffset RegisteredAt);
