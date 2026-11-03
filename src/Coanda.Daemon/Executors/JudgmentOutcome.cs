using Coanda.Core.Flights;

namespace Coanda.Daemon.Executors;

/// <summary>
/// How one judgment ended: a valid answer, or a park with a reason and a lever. Either carries the
/// session the judgment ran in, which is still open so the runner can stop it or keep it for a
/// reattach, and the usage of every attempt summed, since a retry's cost counts against the flow's
/// budget too.
/// </summary>
public abstract record JudgmentOutcome(SessionHandle Session, Measurement Usage)
{
    /// <summary>The agent answered with a document its schema accepts.</summary>
    public sealed record Answered(SessionHandle Session, string Answer, string TranscriptPath, Measurement Usage)
        : JudgmentOutcome(Session, Usage);

    /// <summary>The agent gave no valid answer after its one retry; the lever is the transcript path.</summary>
    public sealed record Parked(SessionHandle Session, string Reason, string Lever, Measurement Usage)
        : JudgmentOutcome(Session, Usage);
}
