using Coanda.Core.Projects;

namespace Coanda.Daemon.Api.Endpoints;

public sealed record ProjectAddRequest(string Name, string Remote, string PrimaryBranch);

/// <summary>Names the credential profile of the running daemon's configuration that a project's judgments spawn with.</summary>
public sealed record ProjectSetProfileRequest(string Project, string Profile);

/// <summary>A registered project; <see cref="Profile"/> is the credential profile it names on this node, or null when it names none.</summary>
public sealed record ProjectView(string Name, string Remote, string PrimaryBranch, string RegisteredBy, DateTimeOffset RegisteredAt, string? Profile)
{
    public static ProjectView From(Project project, string? profile) =>
        new(project.Name, project.Remote, project.PrimaryBranch, project.RegisteredBy, project.RegisteredAt, profile);
}
