using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Projects;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Registers the projects a test creates parcels in, since a parcel is created only in a
/// registered project. The remote and the branch are placeholders: only the worktree step reads
/// them, and every test runs it on the scripted command runner, which contacts nothing.
/// </summary>
public static class RegisteredProjects
{
    public const string Remote = "https://example.invalid/repo.git";
    public const string Branch = "main";

    /// <summary>Registers <paramref name="project"/> in a store opened without the daemon.</summary>
    public static Task AddAsync(IDocumentStore store, string project, CancellationToken cancellationToken) =>
        new ProjectService(store, TimeProvider.System).AddAsync(Principal.LocalOwner, project, Remote, Branch, cancellationToken);

    /// <summary>Registers <paramref name="project"/> through the running daemon's own project service.</summary>
    public static Task AddAsync(RunningDaemon daemon, string project, CancellationToken cancellationToken) =>
        daemon.Services.GetRequiredService<ProjectService>().AddAsync(Principal.LocalOwner, project, Remote, Branch, cancellationToken);
}
