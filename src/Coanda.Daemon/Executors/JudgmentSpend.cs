using Coanda.Core.Flows;

namespace Coanda.Daemon.Executors;

/// <summary>
/// What one judgment's session has reported spending so far, kept as it is reported: the usage of
/// every attempt a collect returned, and the usage the executor reported for the attempt in
/// progress when the judgment stopped the session after a failure. A judgment that ends with no
/// outcome (a person's pause or stop, a wall-clock limit, a failure after the spawn) still says
/// through it what it spent. The judgment writes it and its caller reads it once the judgment is over.
/// </summary>
public sealed class JudgmentSpend
{
    /// <summary>The sum of every report, or null while there has been none.</summary>
    public Measurement? Reported { get; private set; }

    internal void Add(Measurement? usage)
    {
        if (usage is not null)
        {
            Reported = Reported is null ? usage : Judgment.Sum(Reported, usage);
        }
    }
}
