using Coanda.Core.Flights;

namespace Coanda.Daemon.Executors;

/// <summary>
/// How one judgment ended: a valid answer, or a park with a reason and a lever. Either carries the
/// usage of every attempt summed, since a retry's cost counts against the flow's budget too.
/// </summary>
public abstract record JudgmentOutcome(Measurement Usage)
{
    /// <summary>The agent answered with a document its schema accepts.</summary>
    public sealed record Answered(string Answer, string TranscriptPath, Measurement Usage) : JudgmentOutcome(Usage);

    /// <summary>The agent gave no valid answer after its one retry; the lever is the transcript path.</summary>
    public sealed record Parked(string Reason, string Lever, Measurement Usage) : JudgmentOutcome(Usage);
}
