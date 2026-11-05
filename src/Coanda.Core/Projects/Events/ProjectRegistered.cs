using System.Text.Json.Serialization;

namespace Coanda.Core.Projects.Events;

/// <summary>
/// A project was registered on the node: its name and its repos, each a name and a git remote, the
/// remote recorded exactly as given.
/// </summary>
/// <remarks>
/// A build from before repos recorded the remote of the project's one repository in
/// <see cref="Remote"/> and left <see cref="Repos"/> out, beside a primary branch and build and test
/// commands, which this record no longer reads; <see cref="ReposRecorded"/> reads such a registration
/// as one repo at that remote. This build never writes <see cref="Remote"/>.
/// </remarks>
public sealed record ProjectRegistered(
    Guid ProjectId,
    string Name,
    IReadOnlyList<ProjectRepo>? Repos,
    string RegisteredBy,
    DateTimeOffset RegisteredAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Remote = null)
{
    /// <summary>The repos the registration recorded, which for one an earlier build wrote is the one repo at its remote.</summary>
    [JsonIgnore]
    public IReadOnlyList<ProjectRepo> ReposRecorded =>
        (Repos, Remote) switch
        {
            ({ } repos, _) => repos,
            (null, { } remote) => [ProjectRepo.FromRemote(remote)],
            _ => [],
        };
}
