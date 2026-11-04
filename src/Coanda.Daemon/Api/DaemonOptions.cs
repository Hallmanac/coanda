using Coanda.Core.Parcels;
using Coanda.Daemon.Executors;

namespace Coanda.Daemon.Api;

/// <summary>
/// What the daemon host accepts when it is started. <c>coa daemon run</c> gets the executor and the
/// credential profiles from the node configuration (<see cref="NodeConfiguration"/>) and the
/// defaults for the rest; a test builds these directly.
/// </summary>
public sealed record DaemonOptions
{
    public static readonly TimeSpan DefaultJudgmentLimit = TimeSpan.FromMinutes(30);

    /// <summary>The executor judgments run through; null runs them through a scripted executor with no entries, so every judgment pauses.</summary>
    public IAgentExecutor? Executor { get; init; }

    /// <summary>
    /// The credential profiles the node holds, by vendor and name; a judgment in a project that
    /// names one spawns with it. None when none is given.
    /// </summary>
    public IReadOnlyList<CredentialProfile> Profiles { get; init; } = [];

    /// <summary>
    /// The gates the daemon runs beside the shipped gate set, which it always runs; moves pass
    /// through them and drafted parcel types are validated against them. None by default.
    /// </summary>
    public IReadOnlyList<IGate> Gates { get; init; } = [];

    /// <summary>How long one judgment, its retry included, may run before its session is stopped and the flow pauses.</summary>
    public TimeSpan JudgmentLimit { get; init; } = DefaultJudgmentLimit;
}
