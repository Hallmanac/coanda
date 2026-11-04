using System.Collections.Immutable;

namespace Coanda.Core.Flows;

/// <summary>
/// Where a flow run stands: the current step, how many times each step has been entered (the current
/// visit included), and how many agents the flow run has spawned so far. A position is a value: it
/// never changes, and two positions holding the same counts are equal. Build one with
/// <see cref="Interpreter.Start"/> and advance it with <see cref="Interpreter.Apply"/>.
/// </summary>
public sealed record FlowRunPosition(string Step, ImmutableDictionary<string, int> Visits, int Spawns)
{
    /// <summary>How many times the named step has been entered; zero when it never has.</summary>
    public int VisitsTo(string step) => Visits.GetValueOrDefault(step);

    public bool Equals(FlowRunPosition? other) =>
        other is not null
        && string.Equals(Step, other.Step, StringComparison.Ordinal)
        && Spawns == other.Spawns
        && Visits.Count == other.Visits.Count
        && Visits.All(visit => other.Visits.TryGetValue(visit.Key, out int count) && count == visit.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Step, StringComparer.Ordinal);
        hash.Add(Spawns);
        foreach (KeyValuePair<string, int> visit in Visits.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            hash.Add(visit.Key, StringComparer.Ordinal);
            hash.Add(visit.Value);
        }

        return hash.ToHashCode();
    }
}
