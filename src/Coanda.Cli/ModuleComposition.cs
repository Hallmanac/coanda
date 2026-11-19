using Coanda.Daemon.Api;
using Coanda.Daemon.Modules;
using Coanda.Modules.Software;
using Coanda.Modules.Software.GitHub;

namespace Coanda.Cli;

/// <summary>
/// The one place the executable composes the modules its daemon hosts: it builds the software module,
/// gives it its options, and hands it to the daemon with the modules a new project is switched on for
/// when the node configuration names none.
/// The daemon names no module, so this is the only file outside <c>coa project add</c>'s command that
/// names a module's types. <c>coa daemon run</c> starts its daemon through it, and so does every test
/// that starts one.
/// </summary>
internal static class ModuleComposition
{
    /// <summary>How long the gates step's build command, and then its test command, may each run unless the host says otherwise.</summary>
    public static readonly TimeSpan DefaultGatesCommandLimit = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The modules a project registered on this node is switched on for when the node configuration names
    /// none: the software module alone.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultModules = [SoftwareSteps.Module];

    /// <summary>
    /// Where the build puts the software module's <c>module.json</c> and definitions folder: under
    /// <c>modules/software/</c> beside the executable's assembly, copied there from the module's project.
    /// </summary>
    public static string SoftwareModulePath => Path.Combine(AppContext.BaseDirectory, "modules", SoftwareSteps.Module);

    /// <summary>
    /// <paramref name="options"/> with the software module composed ahead of any module they already
    /// compose, built as <paramref name="software"/> says. The modules a new project is switched on for
    /// are those the options already name, which are the node configuration's <c>defaultModules</c>, or
    /// <see cref="DefaultModules"/> when they name none; an empty list is kept as none. A gates command
    /// limit that is not positive is refused here, before any daemon starts.
    /// </summary>
    public static DaemonOptions Compose(DaemonOptions options, SoftwareHosting software)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(software);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(software.GatesCommandLimit, TimeSpan.Zero, nameof(software));
        Func<HttpMessageHandler> gitHubHandler = software.GitHubHandler ?? (() => new NoGitHubHandler());
        ComposedModule composed = ComposedModule.Of(
            SoftwareSteps.Module,
            software.ModulePath ?? SoftwareModulePath,
            context => new SoftwareModule(new SoftwareModuleOptions
            {
                HomePath = context.HomePath,
                ProfilesPath = context.AreaPath,
                CoreApi = context.CoreApi,
                CommandRunner = context.CommandRunner,
                GitHubHandler = gitHubHandler,
                LoggerFactory = context.LoggerFactory,
                GatesCommandLimit = software.GatesCommandLimit,
            }));
        return options with
        {
            ComposedModules = [composed, .. options.ComposedModules],
            DefaultModules = options.DefaultModules ?? DefaultModules,
        };
    }
}

/// <summary>What the host gives the software module beside what the daemon gives every composed module.</summary>
internal sealed record SoftwareHosting
{
    /// <summary>What <c>coa daemon run</c> hosts the module with: its GitHub client reaches the network.</summary>
    public static readonly SoftwareHosting Networked = new() { GitHubHandler = GitHubClient.NetworkHandler };

    /// <summary>
    /// The factory of the primary message handler the module's GitHub client sends its requests through,
    /// called each time the client factory makes a handler, so it returns a new one each call; null uses
    /// <see cref="NoGitHubHandler"/>, which fails every request, so the open-pr step pauses.
    /// <c>coa daemon run</c> supplies <see cref="GitHubClient.NetworkHandler"/>.
    /// </summary>
    public Func<HttpMessageHandler>? GitHubHandler { get; init; }

    /// <summary>
    /// The directory the module's <c>module.json</c> and definitions folder are read from at start, whose
    /// definitions are imported in module scope; null reads <see cref="ModuleComposition.SoftwareModulePath"/>.
    /// </summary>
    public string? ModulePath { get; init; }

    /// <summary>
    /// How long the gates step's build command, and then its test command, may each run before it is
    /// ended and the step answers failed, so a command that never exits sends the work to its fix lap.
    /// </summary>
    public TimeSpan GatesCommandLimit { get; init; } = ModuleComposition.DefaultGatesCommandLimit;
}
