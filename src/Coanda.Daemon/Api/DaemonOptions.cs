using Coanda.Core.Parcels;
using Coanda.Sdk.Commands;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Modules;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Triggers;
using Coanda.Sdk.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Api;

/// <summary>
/// What the daemon host accepts when it is started. <c>coa daemon run</c> gets the executor and the
/// credential profiles from the node configuration (<see cref="NodeConfiguration"/>), supplies the
/// command runner that starts real processes, and has the host that builds the executable add the modules
/// it composes and the modules a new project is switched on for; it takes the defaults for the rest. A
/// test builds these directly, or through the same host.
/// </summary>
public sealed record DaemonOptions
{
    public static readonly TimeSpan DefaultAgentStepLimit = TimeSpan.FromMinutes(30);

    /// <summary>The executor agent steps run through; null runs them through a scripted executor with no entries, so every agent step pauses.</summary>
    public IAgentExecutor? Executor { get; init; }

    /// <summary>
    /// The runner every external command goes through: the core's clones and fetches of each repo, and
    /// whatever a composed module runs, since it is given this runner (<see cref="ModuleHostContext.CommandRunner"/>);
    /// null uses <see cref="NoCommandRunner"/>, which starts nothing and fails every command.
    /// </summary>
    public ICommandRunner? CommandRunner { get; init; }

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
    /// Modules the host composes, which the daemon builds at start from the context it gives each one and
    /// hosts in its own process, in this order: each gets its module area under the node home and a core
    /// token issued for the start, and its definitions are imported in module scope from its directory.
    /// A host adapter carries every call to it, as it does to a module in <see cref="Modules"/>. None by
    /// default: <c>coa daemon run</c> has the host that builds the executable add its own.
    /// </summary>
    public IReadOnlyList<ComposedModule> ComposedModules { get; init; } = [];

    /// <summary>
    /// The names of the modules a project registered on this node is switched on for, which a project
    /// registered before modules were also has switched on at start: the node configuration's
    /// <c>defaultModules</c>, or null when it names none, which leaves the list to the host that builds
    /// the executable. A daemon started with null switches on none. Every name must be a module the
    /// daemon hosts, or the start is refused before anything is recorded.
    /// </summary>
    public IReadOnlyList<string>? DefaultModules { get; init; }

    /// <summary>
    /// Modules the daemon hosts in its own process beside the composed ones, by name, already built, each
    /// called through a host adapter as a composed module is: waited on for health and asked to describe
    /// itself at start, dispatched the steps it describes, and fed the core events it subscribes to. Each
    /// gets a module area and is issued no core token. None by default: a test brings stand-in modules here.
    /// </summary>
    public IReadOnlyDictionary<string, IModule> Modules { get; init; } = new Dictionary<string, IModule>();

    /// <summary>
    /// The settings schemas of modules the daemon does not host, by module name, read beside the hosted
    /// modules' own; null holds none. A test brings stand-in modules here.
    /// </summary>
    public IModuleSettingsSchemas? ModuleSettingsSchemas { get; init; }

    /// <summary>
    /// Directories of modules the daemon does not host, each holding a <c>module.json</c> and a
    /// definitions folder, whose definitions it imports at start after the composed modules'. None by
    /// default: a test brings stand-in modules here.
    /// </summary>
    public IReadOnlyList<string> ModulePaths { get; init; } = [];

    /// <summary>A logger provider the daemon's log goes to beside the console, or null for the console alone.</summary>
    public ILoggerProvider? LoggerProvider { get; init; }

    /// <summary>
    /// How often the daemon exports the events it authored in each project and pushes them from every
    /// clone of the project's repos; null runs no export loop, though a service call still exports a
    /// project at once. <c>coa daemon run</c> supplies <see cref="Transport.LedgerExport.DefaultInterval"/>
    /// unless the node configuration sets <c>ledgerExport</c> to false (<see cref="NodeConfiguration"/>).
    /// </summary>
    public TimeSpan? ExportInterval { get; init; }

    /// <summary>
    /// How often the daemon fetches its peers' ledger refs into every clone of each project's repos and
    /// imports their lines; null runs no import loop, though a service call still imports a project at once.
    /// <c>coa daemon run</c> supplies <see cref="Transport.LedgerImport.DefaultInterval"/> unless the node
    /// configuration sets <c>ledgerImport</c> to false (<see cref="NodeConfiguration"/>).
    /// </summary>
    public TimeSpan? ImportInterval { get; init; }

    /// <summary>How long one agent step, its retry included, may run before its session is stopped and the flow run pauses.</summary>
    public TimeSpan AgentStepLimit { get; init; } = DefaultAgentStepLimit;

    /// <summary>
    /// What the trigger loop awaits after each follower of a flow run's end is decided and before the
    /// next one; null, as <c>coa daemon run</c> leaves it, awaits nothing. Only a test supplies it.
    /// </summary>
    internal Func<FollowChoice, CancellationToken, Task>? FollowDecided { get; init; }

    /// <summary>
    /// What the trigger loop awaits just before it starts a follow from a parcel's follow queue; null, as
    /// <c>coa daemon run</c> leaves it, awaits nothing. Only a test supplies it, to fail or hold open that start.
    /// </summary>
    internal Func<DeferredFollowStart, CancellationToken, Task>? DeferredFollowStarting { get; init; }
}
