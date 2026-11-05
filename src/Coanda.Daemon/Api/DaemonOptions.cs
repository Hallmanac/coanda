using Coanda.Core.Parcels;
using Coanda.Sdk.Commands;
using Coanda.Daemon.Executors;
using Coanda.Daemon.GitHub;
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
    /// The runner every external command of the worktree, gates, and open-pr system steps and a worktree's
    /// removal goes through; null uses <see cref="NoCommandRunner"/>, which starts nothing and fails
    /// every command, so those steps pause.
    /// </summary>
    public ICommandRunner? CommandRunner { get; init; }

    /// <summary>
    /// The factory of the primary message handler the GitHub client sends its requests through,
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

    /// <summary>A logger provider the daemon's log goes to beside the console, or null for the console alone.</summary>
    public ILoggerProvider? LoggerProvider { get; init; }

    /// <summary>How long one agent step, its retry included, may run before its session is stopped and the flow run pauses.</summary>
    public TimeSpan AgentStepLimit { get; init; } = DefaultAgentStepLimit;

    /// <summary>
    /// How long the gates step's build command, and then its test command, may each run before it is
    /// ended and the step answers failed, so a command that never exits sends the work to its fix lap.
    /// </summary>
    public TimeSpan GatesCommandLimit { get; init; } = DefaultGatesCommandLimit;
}
