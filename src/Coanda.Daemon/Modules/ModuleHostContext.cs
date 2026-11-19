using Coanda.Sdk.Client;
using Coanda.Sdk.Commands;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Modules;

/// <summary>
/// What the daemon gives a module the host composes (<see cref="ComposedModule"/>) when it builds it at start.
/// Nothing else about the daemon reaches the module: every call on it carries only the module API's
/// documents, and it reads the rest through the core's API.
/// </summary>
/// <param name="HomePath">The node home's path, under which the core keeps each repo's clone.</param>
/// <param name="AreaPath">The module's own area of the node home, made owner-only before the module is built.</param>
/// <param name="CoreApi">
/// The core's address and the module token issued to the module for this start. Both exist only once the
/// daemon listens, so this is read when the module first calls the core; read before that, it throws.
/// </param>
/// <param name="CommandRunner">The runner the module starts its external commands through.</param>
/// <param name="LoggerFactory">Where the module's log goes.</param>
public sealed record ModuleHostContext(
    string HomePath,
    string AreaPath,
    Func<CoreApiOptions> CoreApi,
    ICommandRunner CommandRunner,
    ILoggerFactory LoggerFactory);
