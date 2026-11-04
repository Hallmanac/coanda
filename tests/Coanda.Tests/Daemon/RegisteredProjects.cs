using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Projects;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Registers the projects a test creates parcels in, since a parcel is created only in a
/// registered project. The remote and the branch are placeholders: only the worktree and open-pr
/// steps read them, and every test runs those on the scripted command runner and a fake GitHub,
/// which contact nothing. The remote is in a github.com form, so open-pr reads it. Each project names
/// <see cref="RequiredCheck"/> as the one check its merge waits for, the check the fake GitHub
/// reports as succeeded by default, so a build that runs to its end is merged and its task closed.
/// </summary>
public static class RegisteredProjects
{
    public const string Remote = "https://github.com/example/repo.git";
    public const string Branch = "main";
    public const string RequiredCheck = "build-test (ubuntu-latest)";

    /// <summary>Registers <paramref name="project"/> in a store opened without the daemon.</summary>
    public static Task AddAsync(IDocumentStore store, string project, CancellationToken cancellationToken) =>
        AddAsync(new ProjectService(store, TimeProvider.System), project, cancellationToken);

    /// <summary>Registers <paramref name="project"/> through the running daemon's own project service.</summary>
    public static Task AddAsync(RunningDaemon daemon, string project, CancellationToken cancellationToken) =>
        AddAsync(daemon.Services.GetRequiredService<ProjectService>(), project, cancellationToken);

    private static async Task AddAsync(ProjectService projects, string project, CancellationToken cancellationToken)
    {
        await projects.AddAsync(Principal.LocalOwner, project, Remote, Branch, cancellationToken);
        await projects.SetRequiredChecksAsync(Principal.LocalOwner, project, [RequiredCheck], cancellationToken);
    }
}
