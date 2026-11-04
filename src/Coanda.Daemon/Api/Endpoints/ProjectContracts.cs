using Coanda.Core.Projects;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>Registers a project; a null <see cref="Build"/> or <see cref="Test"/> leaves its gates on the default command.</summary>
public sealed record ProjectAddRequest(string Name, string Remote, string PrimaryBranch, string? Build = null, string? Test = null);

/// <summary>Names the credential profile of the running daemon's configuration that a project's agent steps spawn with.</summary>
public sealed record ProjectSetProfileRequest(string Project, string Profile);

/// <summary>Replaces the checks a merge of the project's pull requests waits for with <see cref="Checks"/>, each recorded exactly as given.</summary>
public sealed record ProjectRequiredChecksRequest(IReadOnlyList<string> Checks);

/// <summary>
/// A registered project; <see cref="RequiredChecks"/> are the checks a merge of its pull requests
/// waits for, empty until they are set; <see cref="Profile"/> is the credential profile it names on this node, or
/// null when it names none, and <see cref="Build"/> and <see cref="Test"/> are the commands its gates
/// run, the defaults included.
/// </summary>
public sealed record ProjectView(
    string Name,
    string Remote,
    string PrimaryBranch,
    string RegisteredBy,
    DateTimeOffset RegisteredAt,
    string? Profile,
    string Build,
    string Test,
    IReadOnlyList<string> RequiredChecks)
{
    public static ProjectView From(Project project, string? profile) =>
        new(project.Name, project.Remote, project.PrimaryBranch, project.RegisteredBy, project.RegisteredAt, profile, project.Build, project.Test, project.RequiredChecks);
}
