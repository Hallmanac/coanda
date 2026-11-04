using Coanda.Core.Parcels;
using Coanda.Daemon.Executors;

namespace Coanda.Daemon.Api;

/// <summary>
/// What the daemon host accepts when it is started. No command-line flag or file sets these yet;
/// the defaults are what <c>coa daemon run</c> gets.
/// </summary>
public sealed record DaemonOptions
{
    public static readonly TimeSpan DefaultJudgmentLimit = TimeSpan.FromMinutes(30);

    /// <summary>The executor judgments run through; null runs them through a scripted executor with no entries, so every judgment pauses.</summary>
    public IAgentExecutor? Executor { get; init; }

    /// <summary>
    /// The gates the daemon runs, which moves pass through and drafted parcel types are validated
    /// against; none when none is given, which is the shipped gate set.
    /// </summary>
    public IReadOnlyList<IGate> Gates { get; init; } = [];

    /// <summary>How long one judgment, its retry included, may run before its session is stopped and the flow pauses.</summary>
    public TimeSpan JudgmentLimit { get; init; } = DefaultJudgmentLimit;
}
