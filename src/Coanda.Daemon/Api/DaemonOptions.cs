using Coanda.Daemon.Executors;

namespace Coanda.Daemon.Api;

/// <summary>
/// What the daemon host accepts when it is started. No command-line flag or file sets these yet;
/// the defaults are what <c>coa daemon run</c> gets.
/// </summary>
public sealed record DaemonOptions
{
    public static readonly TimeSpan DefaultJudgmentLimit = TimeSpan.FromMinutes(30);

    /// <summary>The executor judgments run through; null runs them through a scripted executor with no entries, so every judgment parks.</summary>
    public IAgentExecutor? Executor { get; init; }

    /// <summary>How long one judgment, its retry included, may run before its session is stopped and the flight parks.</summary>
    public TimeSpan JudgmentLimit { get; init; } = DefaultJudgmentLimit;
}
