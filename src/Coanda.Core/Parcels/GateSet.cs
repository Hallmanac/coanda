namespace Coanda.Core.Parcels;

/// <summary>
/// The gates a daemon runs, by name. A parcel type is validated against <see cref="Names"/>, and
/// a move is refused when a gate it binds is not here, so a missing gate fails closed.
/// </summary>
public sealed class GateSet
{
    private readonly Dictionary<string, IGate> gates = new(StringComparer.Ordinal);

    public GateSet(IEnumerable<IGate> gates)
    {
        foreach (IGate gate in gates)
        {
            if (!this.gates.TryAdd(gate.Name, gate))
            {
                throw new InvalidOperationException($"The gate '{gate.Name}' is given twice; each name has one gate.");
            }
        }

        Names = this.gates.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>No gates: what a daemon runs when it is given none. The shipped gate set is empty.</summary>
    public static GateSet None { get; } = new([]);

    public IReadOnlySet<string> Names { get; }

    public IGate? Find(string name) => gates.GetValueOrDefault(name);
}
