using Coanda.Core.Projects;
using Coanda.Daemon.Modules;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>
/// Registers a project with one repo, at <see cref="Remote"/>, and records <see cref="Settings"/>, each a
/// replicated setting of a module the registration switches on, in the same save. Every setting is
/// checked against its module's settings schema, and one that fails registers nothing.
/// </summary>
public sealed record ProjectAddRequest(string Name, string Remote, IReadOnlyList<ModuleSettingValue>? Settings = null);

/// <summary>Names the credential profile of the running daemon's configuration that a project's agent steps spawn with.</summary>
public sealed record ProjectSetProfileRequest(string Project, string Profile);

/// <summary>One of a project's repos: the name it goes by and its git remote.</summary>
public sealed record ProjectRepoView(string Name, string Remote)
{
    public static ProjectRepoView From(ProjectRepo repo) => new(repo.Name, repo.Remote);
}

/// <summary>
/// A registered project: its name, its repos, who registered it and when, and <see cref="Profile"/>, the
/// credential profile its agent steps spawn with on this node, or null when it names none. A module's
/// settings for the project are shown by the module settings call, never here.
/// </summary>
public sealed record ProjectView(
    string Name,
    IReadOnlyList<ProjectRepoView> Repos,
    string RegisteredBy,
    DateTimeOffset RegisteredAt,
    string? Profile)
{
    public static ProjectView From(Project project, string? profile) =>
        new(project.Name, [.. project.Repos.Select(ProjectRepoView.From)], project.RegisteredBy, project.RegisteredAt, profile);
}
