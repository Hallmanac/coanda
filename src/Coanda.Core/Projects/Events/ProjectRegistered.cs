namespace Coanda.Core.Projects.Events;

/// <summary>
/// A project was registered on the node: its name, the remote of its one repository, that
/// repository's primary branch, and the build and test commands its gates run, each null when the
/// registration named none. All four are recorded exactly as given.
/// </summary>
public sealed record ProjectRegistered(
    Guid ProjectId,
    string Name,
    string Remote,
    string PrimaryBranch,
    string RegisteredBy,
    DateTimeOffset RegisteredAt,
    string? BuildCommand = null,
    string? TestCommand = null);
