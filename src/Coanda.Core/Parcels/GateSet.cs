namespace Coanda.Core.Parcels;

/// <summary>
/// The gates a daemon runs, by name: <see cref="Shipped"/>, and any more it is given. A parcel type
/// is validated against <see cref="Names"/>, and a move is refused when a gate it binds is not here,
/// so a missing gate fails closed.
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

    /// <summary>The gates the binary carries, which every daemon runs whatever else it is given.</summary>
    public static IReadOnlyList<IGate> ShippedGates { get; } = [new RequiredFieldsGate()];

    /// <summary>
    /// The shipped gate set: what seeding, drafting, and the parcel and definition services use when
    /// they are given no gates, and what a daemon runs beside any gates its options add.
    /// </summary>
    public static GateSet Shipped { get; } = new(ShippedGates);

    /// <summary>No gates at all, for a check that a type binding a gate is refused where that gate is not run.</summary>
    public static GateSet None { get; } = new([]);

    public IReadOnlySet<string> Names { get; }

    public IGate? Find(string name) => gates.GetValueOrDefault(name);
}
