using Coanda.Core.Projects;

namespace Coanda.Daemon.Api.Endpoints;

public sealed record ProjectAddRequest(string Name, string Remote, string PrimaryBranch);

public sealed record ProjectView(string Name, string Remote, string PrimaryBranch, string RegisteredBy, DateTimeOffset RegisteredAt)
{
    public static ProjectView From(Project project) =>
        new(project.Name, project.Remote, project.PrimaryBranch, project.RegisteredBy, project.RegisteredAt);
}
