using System.Text.Json;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Modules.Software;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Registers the projects a test creates parcels in, since a parcel is created only in a
/// registered project. The remote and the branch are placeholders: only the worktree and open-pr
/// steps read them, and every test runs those on the scripted command runner and a fake GitHub,
/// which contact nothing. The remote is in a github.com form, so open-pr reads it, and it names the
/// repo <see cref="Repo"/>. Each project is registered with the software module's settings
/// <see cref="Settings"/>: <see cref="Branch"/> as its repo's primary branch, and
/// <see cref="RequiredCheck"/> as the one check its merge waits for, the check the fake GitHub
/// reports as succeeded by default, so a build that runs to its end is merged and its task closed.
/// </summary>
public static class RegisteredProjects
{
    public const string Remote = "https://github.com/example/repo.git";
    public const string Repo = "repo";
    public const string Branch = "main";
    public const string RequiredCheck = "build-test (ubuntu-latest)";

    /// <summary>The settings each project is registered with: the primary branch, and the required check.</summary>
    public static IReadOnlyList<ModuleSettingValue> Settings { get; } =
    [
        Setting(SoftwareSettings.PrimaryBranch, new Dictionary<string, string> { [Repo] = Branch }),
        Setting(SoftwareSettings.RequiredChecks, new[] { RequiredCheck }),
    ];

    /// <summary>The settings a registration through the CLI's options sends: the primary branch alone.</summary>
    public static IReadOnlyList<ModuleSettingValue> BranchOnly { get; } =
        [Setting(SoftwareSettings.PrimaryBranch, new Dictionary<string, string> { [Repo] = Branch })];

    /// <summary>The software module's setting <paramref name="name"/> with <paramref name="value"/> as its JSON.</summary>
    public static ModuleSettingValue Setting(string name, object value) =>
        new(SoftwareSteps.Module, name, JsonSerializer.SerializeToElement(value));

    /// <summary>Registers <paramref name="project"/> in a store opened without the daemon.</summary>
    public static Task<Project> AddAsync(IDocumentStore store, string project, CancellationToken cancellationToken) =>
        new ProjectService(store, TimeProvider.System).AddAsync(Principal.LocalOwner, project, Remote, Settings, cancellationToken);

    /// <summary>Registers <paramref name="project"/> through the running daemon's own project service.</summary>
    public static Task<Project> AddAsync(RunningDaemon daemon, string project, CancellationToken cancellationToken) =>
        AddAsync(daemon, project, Settings, cancellationToken);

    /// <summary>Registers <paramref name="project"/> through the running daemon's own project service with the module settings <paramref name="settings"/>.</summary>
    public static Task<Project> AddAsync(RunningDaemon daemon, string project, IReadOnlyList<ModuleSettingValue> settings, CancellationToken cancellationToken) =>
        daemon.Services.GetRequiredService<ProjectService>().AddAsync(Principal.LocalOwner, project, Remote, settings, cancellationToken);
}
