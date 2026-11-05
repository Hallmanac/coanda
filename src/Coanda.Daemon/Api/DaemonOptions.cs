using Coanda.Core.Parcels;
using Coanda.Sdk.Commands;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Modules;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Modules.Software.GitHub;
using Coanda.Sdk.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Api;

/// <summary>
/// What the daemon host accepts when it is started. <c>coa daemon run</c> gets the executor and the
/// credential profiles from the node configuration (<see cref="NodeConfiguration"/>), supplies the
/// command runner that starts real processes and the GitHub handler that reaches the network, and
/// takes the defaults for the rest; a test builds these directly.
/// </summary>
public sealed record DaemonOptions
{
    public static readonly TimeSpan DefaultAgentStepLimit = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan DefaultGatesCommandLimit = TimeSpan.FromMinutes(60);

    /// <summary>The executor agent steps run through; null runs them through a scripted executor with no entries, so every agent step pauses.</summary>
    public IAgentExecutor? Executor { get; init; }

    /// <summary>
    /// The runner the software module runs every external command of its worktree, gates, open-pr, and
    /// rebase steps and of a worktree's removal through; null uses <see cref="NoCommandRunner"/>, which
    /// starts nothing and fails every command, so those steps pause.
    /// </summary>
    public ICommandRunner? CommandRunner { get; init; }

    /// <summary>
    /// The factory of the primary message handler the software module's GitHub client sends its requests through,
    /// called each time the client factory makes a handler, so it returns a new one each call; null
    /// uses <see cref="NoGitHubHandler"/>, which fails every request, so the open-pr step pauses.
    /// <c>coa daemon run</c> supplies <see cref="GitHubClient.NetworkHandler"/>.
    /// </summary>
    public Func<HttpMessageHandler>? GitHubHandler { get; init; }

    /// <summary>
    /// The credential profiles the node holds, by vendor and name; an agent step in a project that
    /// names one spawns with it. None when none is given.
    /// </summary>
    public IReadOnlyList<CredentialProfile> Profiles { get; init; } = [];

    /// <summary>
    /// The gates the daemon runs beside the shipped gate set, which it always runs; moves pass
    /// through them and drafted parcel types are validated against them. None by default.
    /// </summary>
    public IReadOnlyList<IGate> Gates { get; init; } = [];

    /// <summary>
    /// System steps the daemon implements beside the core's, which it always implements, and which flows
    /// in any project may name; a name that is already implemented is refused. None by default: a test
    /// brings a step of its own here.
    /// </summary>
    public IReadOnlyList<IFlowSystemStep> SystemSteps { get; init; } = [];

    /// <summary>
    /// Modules the daemon hosts in its own process beside the software module, by name, each called
    /// through a host adapter as the software module is: waited on for health and asked to describe
    /// itself at start, dispatched the steps it describes, and fed the core events it subscribes to.
    /// None by default: a test brings stand-in modules here.
    /// </summary>
    public IReadOnlyDictionary<string, IModule> Modules { get; init; } = new Dictionary<string, IModule>();

    /// <summary>
    /// The settings schemas of modules the daemon does not host, by module name, read beside the hosted
    /// modules' own; null holds none. A test brings stand-in modules here.
    /// </summary>
    public IModuleSettingsSchemas? ModuleSettingsSchemas { get; init; }

    /// <summary>
    /// The directory the software module's <c>module.json</c> and definitions folder are read from at
    /// start, whose definitions are imported in module scope; null reads <see cref="DefaultSoftwareModulePath"/>.
    /// </summary>
    public string? SoftwareModulePath { get; init; }

    /// <summary>
    /// Directories of modules the daemon does not host, each holding a <c>module.json</c> and a
    /// definitions folder, whose definitions it imports at start after the software module's. None by
    /// default: a test brings stand-in modules here.
    /// </summary>
    public IReadOnlyList<string> ModulePaths { get; init; } = [];

    /// <summary>
    /// Where the build puts the software module's <c>module.json</c> and definitions folder: under
    /// <c>modules/software/</c> beside the daemon's assembly, copied there from the module's project.
    /// </summary>
    public static string DefaultSoftwareModulePath => Path.Combine(AppContext.BaseDirectory, "modules", SoftwareSteps.Module);

    /// <summary>A logger provider the daemon's log goes to beside the console, or null for the console alone.</summary>
    public ILoggerProvider? LoggerProvider { get; init; }

    /// <summary>
    /// How often the daemon exports the events it authored in each project and pushes them from every
    /// clone of the project's repos; null runs no export loop, though a service call still exports a
    /// project at once. <c>coa daemon run</c> supplies <see cref="Transport.LedgerExport.DefaultInterval"/>
    /// unless the node configuration sets <c>ledgerExport</c> to false (<see cref="NodeConfiguration"/>).
    /// </summary>
    public TimeSpan? ExportInterval { get; init; }

    /// <summary>How long one agent step, its retry included, may run before its session is stopped and the flow run pauses.</summary>
    public TimeSpan AgentStepLimit { get; init; } = DefaultAgentStepLimit;

    /// <summary>
    /// How long the gates step's build command, and then its test command, may each run before it is
    /// ended and the step answers failed, so a command that never exits sends the work to its fix lap.
    /// </summary>
    public TimeSpan GatesCommandLimit { get; init; } = DefaultGatesCommandLimit;
}
