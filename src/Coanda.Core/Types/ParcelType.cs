using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coanda.Core.Types;

/// <summary>
/// The definition of a kind of parcel: its lifecycle (states, allowed moves, entry states, the
/// one terminal state and its resolutions), the parked state reachable from any live state, and
/// its bindings: a flow to start when a parcel enters a state, and a gate that must pass before a
/// move. Fields arrive later in phase 1. A type is data; the two shipped types are built in
/// <see cref="ShippedTypes"/> and a user's type is the same shape.
/// </summary>
public sealed record ParcelType
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> States { get; init; }
    public required IReadOnlyList<string> EntryStates { get; init; }
    public required string TerminalState { get; init; }
    public required IReadOnlyList<string> Resolutions { get; init; }
    public required string ParkedState { get; init; }
    /// <summary>Allowed moves between live states, as from-state to the set of to-states.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Moves { get; init; }

    /// <summary>The flow started on a parcel when it enters a state; at most one per state.</summary>
    public IReadOnlyList<FlowBinding> FlowBindings { get; init; } = [];

    /// <summary>The gates that must pass before a move the move table holds.</summary>
    public IReadOnlyList<GateBinding> GateBindings { get; init; } = [];

    /// <summary>Content hash of the definition, recorded on every parcel created under it.</summary>
    public string Hash => ComputeHash();

    public bool IsState(string state) => States.Contains(state, StringComparer.Ordinal);

    public bool IsTerminal(string state) => string.Equals(state, TerminalState, StringComparison.Ordinal);

    public bool IsParked(string state) => string.Equals(state, ParkedState, StringComparison.Ordinal);

    public bool IsEntry(string state) => EntryStates.Contains(state, StringComparer.Ordinal);

    public bool IsResolution(string resolution) => Resolutions.Contains(resolution, StringComparer.Ordinal);

    /// <summary>
    /// The gates a move from <paramref name="from"/> to <paramref name="to"/> must pass. A move out of
    /// the parked state passes every gate bound to a move into the target, so parking bypasses none.
    /// </summary>
    public IReadOnlyList<GateBinding> GatesOn(string from, string to) =>
    [
        .. GateBindings.Where(b => string.Equals(b.To, to, StringComparison.Ordinal)
            && (IsParked(from) || string.Equals(b.From, from, StringComparison.Ordinal))),
    ];

    /// <summary>Whether the move table itself holds a move, as opposed to the park, resume, and close every type allows.</summary>
    public bool HoldsMove(string from, string to) =>
        Moves.TryGetValue(from, out IReadOnlyList<string>? targets) && targets.Contains(to, StringComparer.Ordinal);

    /// <summary>
    /// Whether the lifecycle allows a move. Parking is allowed from any live state; resuming from
    /// parked is allowed to any live state; closing is allowed from any live or parked state and is
    /// expressed by the terminal state; nothing leaves the terminal state.
    /// </summary>
    public bool AllowsMove(string from, string to)
    {
        if (!IsState(from) || !IsState(to) || IsTerminal(from) || string.Equals(from, to, StringComparison.Ordinal))
        {
            return false;
        }

        if (IsTerminal(to) || IsParked(to))
        {
            return true;
        }

        if (IsParked(from))
        {
            return true;
        }

        return HoldsMove(from, to);
    }

    /// <summary>
    /// Refuses a type that breaks a rule, with a <see cref="DomainException"/> quoting it.
    /// <paramref name="knownGates"/> is every gate name the caller runs; a gate binding naming any
    /// other is refused, the way a flow is validated against the action names.
    /// </summary>
    public void Validate(IReadOnlySet<string> knownGates)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainException("A parcel type needs a name.");
        }

        if (States.Distinct(StringComparer.Ordinal).Count() != States.Count)
        {
            throw new DomainException($"Parcel type '{Name}' lists a state twice.");
        }

        if (!IsState(TerminalState))
        {
            throw new DomainException($"Parcel type '{Name}' names a terminal state '{TerminalState}' that is not one of its states.");
        }

        if (!IsState(ParkedState) || IsTerminal(ParkedState))
        {
            throw new DomainException($"Parcel type '{Name}' needs a parked state distinct from its terminal state.");
        }

        if (EntryStates.Count == 0 || EntryStates.Any(s => !IsState(s) || IsTerminal(s) || IsParked(s)))
        {
            throw new DomainException($"Parcel type '{Name}' needs at least one entry state, each a live state.");
        }

        if (Resolutions.Count == 0)
        {
            throw new DomainException($"Parcel type '{Name}' needs at least one resolution for its terminal state.");
        }

        foreach ((string from, IReadOnlyList<string> targets) in Moves)
        {
            if (!IsState(from) || targets.Any(t => !IsState(t)))
            {
                throw new DomainException($"Parcel type '{Name}' has a move naming a state it does not have.");
            }
        }

        ValidateFlowBindings();
        ValidateGateBindings(knownGates);
    }

    private void ValidateFlowBindings()
    {
        foreach (FlowBinding binding in FlowBindings)
        {
            if (!IsState(binding.State) || IsTerminal(binding.State))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' binds a flow to '{binding.State}', which is not one of its live states; a flow binds to a state the type has, never its terminal state.");
            }

            if (string.IsNullOrWhiteSpace(binding.Flow))
            {
                throw new DomainException($"Parcel type '{Name}' binds a flow to '{binding.State}' without naming the flow.");
            }
        }

        string? twice = FlowBindings
            .GroupBy(b => b.State, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (twice is not null)
        {
            throw new DomainException($"Parcel type '{Name}' binds more than one flow to '{twice}'; a state carries at most one flow binding.");
        }
    }

    private void ValidateGateBindings(IReadOnlySet<string> knownGates)
    {
        foreach (GateBinding binding in GateBindings)
        {
            if (!HoldsMove(binding.From, binding.To))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' binds a gate to the move from '{binding.From}' to '{binding.To}', which its move table does not hold; a gate binds only to a move the table holds.");
            }

            if (!knownGates.Contains(binding.Gate))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' binds the gate '{binding.Gate}', which is not a gate this daemon runs; a gate binding names a known gate.");
            }
        }
    }

    /// <summary>
    /// The bindings join the hashed shape only when the type has some, so a type that binds nothing
    /// keeps the hash it had before bindings existed and the parcels created under it still resolve.
    /// </summary>
    private string ComputeHash()
    {
        var lifecycle = new Dictionary<string, object>
        {
            ["name"] = Name,
            ["states"] = States,
            ["entry"] = EntryStates,
            ["terminal"] = TerminalState,
            ["resolutions"] = Resolutions,
            ["parked"] = ParkedState,
            ["moves"] = Moves.OrderBy(m => m.Key, StringComparer.Ordinal).ToDictionary(m => m.Key, m => m.Value),
        };
        if (FlowBindings.Count > 0)
        {
            lifecycle["flows"] = FlowBindings.Select(b => new[] { b.State, b.Flow }).ToList();
        }

        if (GateBindings.Count > 0)
        {
            lifecycle["gates"] = GateBindings.Select(b => new[] { b.From, b.To, b.Gate }).ToList();
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(lifecycle);
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ReadOptions = new() { RespectNullableAnnotations = true };

    public static string CanonicalJson(ParcelType type) => JsonSerializer.Serialize(type, WriteOptions);

    /// <summary>Reads a type from the JSON <see cref="CanonicalJson"/> writes; anything else is a rule broken.</summary>
    public static ParcelType FromJson(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<ParcelType>(content, ReadOptions)
                ?? throw new DomainException("The content is empty; it cannot be read as a parcel type.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new DomainException($"The content cannot be read as a parcel type: {exception.Message}");
        }
    }

    public override string ToString() => $"{Name}@{Hash}";

    public static string Describe(ParcelType type)
    {
        var text = new StringBuilder();
        text.Append(type.Name).Append(": ").AppendJoin(", ", type.States);
        return text.ToString();
    }
}
