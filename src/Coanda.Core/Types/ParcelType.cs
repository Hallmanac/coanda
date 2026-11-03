using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Coanda.Core.Types;

/// <summary>
/// The definition of a kind of parcel: its lifecycle (states, allowed moves, entry states, the
/// one terminal state and its resolutions) and the parked state reachable from any live state.
/// Fields and bindings arrive in phase 1. A type is data; the two shipped types are built in
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

    /// <summary>Content hash of the definition, recorded on every parcel created under it.</summary>
    public string Hash => ComputeHash();

    public bool IsState(string state) => States.Contains(state, StringComparer.Ordinal);

    public bool IsTerminal(string state) => string.Equals(state, TerminalState, StringComparison.Ordinal);

    public bool IsParked(string state) => string.Equals(state, ParkedState, StringComparison.Ordinal);

    public bool IsEntry(string state) => EntryStates.Contains(state, StringComparer.Ordinal);

    public bool IsResolution(string resolution) => Resolutions.Contains(resolution, StringComparer.Ordinal);

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

        return Moves.TryGetValue(from, out IReadOnlyList<string>? targets)
            && targets.Contains(to, StringComparer.Ordinal);
    }

    public void Validate()
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
    }

    private string ComputeHash()
    {
        var canonical = new
        {
            name = Name,
            states = States,
            entry = EntryStates,
            terminal = TerminalState,
            resolutions = Resolutions,
            parked = ParkedState,
            moves = Moves.OrderBy(m => m.Key, StringComparer.Ordinal).ToDictionary(m => m.Key, m => m.Value),
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
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
