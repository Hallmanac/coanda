using Coanda.Sdk.Client;
using Coanda.Sdk.Commands;
using Microsoft.Extensions.Logging;

namespace Coanda.Modules.Software;

/// <summary>
/// What the host gives the software module when it builds it. Nothing else about the daemon reaches the
/// module: every call on it carries only the module API's documents, and the module reads the rest
/// through the core's API.
/// </summary>
public sealed record SoftwareModuleOptions
{
    /// <summary>The node home, where <c>gh</c> runs to read the GitHub token.</summary>
    public required string HomePath { get; init; }

    /// <summary>
    /// The module's area of the node home, holding the credential profiles this node keeps for the module,
    /// one directory per profile, each read only when a project selects it.
    /// </summary>
    public required string ProfilesPath { get; init; }

    /// <summary>The directory under the node home holding one directory per project's clone.</summary>
    public required string ClonesPath { get; init; }

    /// <summary>The directory under the node home holding one directory per project of its flow runs' work paths.</summary>
    public required string WorkPath { get; init; }

    /// <summary>
    /// Where the core's API is and the module token to present to it. It is read when a step first
    /// calls the core, since an in-process host learns its own address only once it is listening; a
    /// module in a process of its own reads it from its environment (<see cref="CoreApiOptions.FromEnvironment"/>).
    /// </summary>
    public required Func<CoreApiOptions> CoreApi { get; init; }

    /// <summary>The runner every git, <c>gh</c>, and gates command goes through.</summary>
    public required ICommandRunner CommandRunner { get; init; }

    /// <summary>
    /// The factory of the primary message handler the GitHub client sends its requests through, called
    /// each time the client factory makes a handler, so it returns a new one each call.
    /// </summary>
    public required Func<HttpMessageHandler> GitHubHandler { get; init; }

    /// <summary>Where the module's log goes.</summary>
    public required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>
    /// How long the gates step's build command, and then its test command, may each run before it is
    /// ended and the step answers failed.
    /// </summary>
    public required TimeSpan GatesCommandLimit { get; init; }
}
