using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coanda.Core.Contours;

namespace Coanda.Core.Types;

/// <summary>
/// The definition of a kind of parcel: its lifecycle (states, allowed moves, entry states, the
/// one terminal state and its resolutions), the paused state reachable from any live state, its
/// fields (each named, of a kind, and required to leave the states it names), and its bindings: a
/// contour to start when a parcel enters a state, and a gate that must pass before a move. A type is
/// data; the types the binary ships and a user's type are the same shape.
/// </summary>
public sealed record ParcelType
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> States { get; init; }
    public required IReadOnlyList<string> EntryStates { get; init; }
    public required string TerminalState { get; init; }
    public required IReadOnlyList<string> Resolutions { get; init; }
    public required string PausedState { get; init; }
    /// <summary>Allowed moves between live states, as from-state to the set of to-states.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Moves { get; init; }

    /// <summary>The contour started on a parcel when it enters a state; at most one per state.</summary>
    public IReadOnlyList<ContourBinding> ContourBindings { get; init; } = [];

    /// <summary>The gates that must pass before a move the move table holds, or before a close from a live state.</summary>
    public IReadOnlyList<GateBinding> GateBindings { get; init; } = [];

    /// <summary>The fields a parcel of the type holds values in, in the order the type declares them.</summary>
    public IReadOnlyList<FieldDeclaration> Fields { get; init; } = [];

    /// <summary>Content hash of the definition, recorded on every parcel created under it; computed, so never written into content.</summary>
    [JsonIgnore]
    public string Hash => ComputeHash();

    public bool IsState(string state) => States.Contains(state, StringComparer.Ordinal);

    public bool IsTerminal(string state) => string.Equals(state, TerminalState, StringComparison.Ordinal);

    public bool IsPaused(string state) => string.Equals(state, PausedState, StringComparison.Ordinal);

    public bool IsEntry(string state) => EntryStates.Contains(state, StringComparer.Ordinal);

    public bool IsResolution(string resolution) => Resolutions.Contains(resolution, StringComparer.Ordinal);

    /// <summary>The field the type declares by that name, or null when it declares none.</summary>
    public FieldDeclaration? Field(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The gates a move from <paramref name="from"/> to <paramref name="to"/> must pass. A move out of
    /// the paused state passes every gate bound to a move into the target, so pausing bypasses none.
    /// </summary>
    public IReadOnlyList<GateBinding> GatesOn(string from, string to) =>
    [
        .. GateBindings.Where(b => string.Equals(b.To, to, StringComparison.Ordinal)
            && (IsPaused(from) || string.Equals(b.From, from, StringComparison.Ordinal))),
    ];

    /// <summary>
    /// The fields, in declaration order, a parcel must hold values in to enter the live state
    /// <paramref name="state"/>: those that every route the move table holds from an entry state to it
    /// requires, before reaching it, to leave some state on the way. An entry state requires nothing to
    /// enter, and neither does a state no route reaches, nor the paused or terminal state. A move the
    /// table holds keeps this by itself, since it checks what its from state requires to leave and a set
    /// field cannot be cleared; a resume out of paused is the one way around that, so it checks this in
    /// addition to what each bound move into the state checks.
    /// </summary>
    public IReadOnlyList<string> RequiredToEnter(string state) => RequiredToEnter(RequiredToEnterByState(), state);

    private IReadOnlyList<string> RequiredToEnter(Dictionary<string, HashSet<string>> byState, string state) =>
        byState.TryGetValue(state, out HashSet<string>? required)
            ? [.. Fields.Select(f => f.Name).Where(required.Contains)]
            : [];

    /// <summary>
    /// A must-hold dataflow over the move table. An entry state starts and stays empty; every other
    /// state a route reaches starts at every declared field and is narrowed, until nothing changes, to
    /// the intersection over its reached predecessors of what each requires to enter plus what it
    /// requires to leave. Intersecting over routes rather than taking a union over predecessors keeps
    /// a move back (review to build, say) from demanding what only the later state needs.
    /// </summary>
    private Dictionary<string, HashSet<string>> RequiredToEnterByState()
    {
        List<(string From, string To)> moves =
        [
            .. Moves.Where(m => IsLive(m.Key))
                .SelectMany(m => m.Value.Where(IsLive).Select(to => (From: m.Key, To: to))),
        ];

        var reached = new HashSet<string>(EntryStates.Where(IsLive), StringComparer.Ordinal);
        var frontier = new Queue<string>(reached);
        while (frontier.TryDequeue(out string? from))
        {
            foreach ((_, string to) in moves.Where(m => string.Equals(m.From, from, StringComparison.Ordinal)))
            {
                if (reached.Add(to))
                {
                    frontier.Enqueue(to);
                }
            }
        }

        HashSet<string> all = [.. Fields.Select(f => f.Name)];
        Dictionary<string, HashSet<string>> required = reached.ToDictionary(
            s => s,
            s => IsEntry(s)
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(all, StringComparer.Ordinal),
            StringComparer.Ordinal);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string state in reached.Where(s => !IsEntry(s)))
            {
                HashSet<string> narrowed = new(all, StringComparer.Ordinal);
                foreach ((string from, _) in moves.Where(m => string.Equals(m.To, state, StringComparison.Ordinal) && reached.Contains(m.From)))
                {
                    narrowed.IntersectWith(required[from].Concat(Fields.Where(f => f.IsRequiredToLeave(from)).Select(f => f.Name)));
                }

                if (!narrowed.SetEquals(required[state]))
                {
                    required[state] = narrowed;
                    changed = true;
                }
            }
        }

        return required;
    }

    private bool IsLive(string state) => IsState(state) && !IsTerminal(state) && !IsPaused(state);

    /// <summary>Whether the move table itself holds a move, as opposed to the pause, resume, and close every type allows.</summary>
    public bool HoldsMove(string from, string to) =>
        Moves.TryGetValue(from, out IReadOnlyList<string>? targets) && targets.Contains(to, StringComparer.Ordinal);

    /// <summary>Whether a gate binding names a close: from a live state other than paused, to the terminal state.</summary>
    private bool IsGatedClose(string from, string to) =>
        IsTerminal(to) && IsState(from) && !IsTerminal(from) && !IsPaused(from);

    /// <summary>
    /// Whether the lifecycle allows a move. Pausing is allowed from any live state; resuming from
    /// paused is allowed to any live state; closing is allowed from any live or paused state and is
    /// expressed by the terminal state; nothing leaves the terminal state.
    /// </summary>
    public bool AllowsMove(string from, string to)
    {
        if (!IsState(from) || !IsState(to) || IsTerminal(from) || string.Equals(from, to, StringComparison.Ordinal))
        {
            return false;
        }

        if (IsTerminal(to) || IsPaused(to))
        {
            return true;
        }

        if (IsPaused(from))
        {
            return true;
        }

        return HoldsMove(from, to);
    }

    /// <summary>
    /// Refuses a type that breaks a rule, with a <see cref="DomainException"/> quoting it.
    /// <paramref name="knownGates"/> is every gate name the caller runs; a gate binding naming any
    /// other is refused, the way a contour is validated against the action names.
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

        if (!IsState(PausedState) || IsTerminal(PausedState))
        {
            throw new DomainException($"Parcel type '{Name}' needs a paused state distinct from its terminal state.");
        }

        if (EntryStates.Count == 0 || EntryStates.Any(s => !IsState(s) || IsTerminal(s) || IsPaused(s)))
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

        ValidateFields();
        ValidateContourBindings();
        ValidateGateBindings(knownGates);
    }

    private void ValidateFields()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldDeclaration? field in Fields)
        {
            // Read content can carry a null element or a missing field, whatever the annotations say.
            if (field is null)
            {
                throw new DomainException($"Parcel type '{Name}' has a field that is null; each field is an object naming the field and its kind.");
            }

            if (!Contour.IsPlainName(field.Name))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' declares a field named '{field.Name}'; a field's name is plain: letters, digits, hyphens, and underscores, and not empty.");
            }

            if (!seen.Add(field.Name))
            {
                throw new DomainException($"Parcel type '{Name}' declares two fields named '{field.Name}'; a type's field names are unique.");
            }

            if (!FieldKinds.IsKnown(field.Kind))
            {
                throw new DomainException(
                    $"Field '{field.Name}' of parcel type '{Name}' is of the kind '{field.Kind}'; a field's kind is one of {string.Join(", ", FieldKinds.All)}.");
            }

            foreach (string? state in field.RequiredToLeave)
            {
                if (state is null || !IsState(state) || IsTerminal(state) || IsPaused(state))
                {
                    throw new DomainException(
                        $"Field '{field.Name}' of parcel type '{Name}' is required to leave '{state}', which is not one of its live states; a field is required to leave only a state the type has, never its terminal or paused state.");
                }
            }
        }
    }

    private void ValidateContourBindings()
    {
        foreach (ContourBinding? binding in ContourBindings)
        {
            // Read content can carry a null element or a missing field, whatever the annotations say.
            if (binding?.State is null || binding.Contour is null)
            {
                throw new DomainException($"Parcel type '{Name}' has a contour binding without a state or a contour; each binding names both.");
            }

            if (!IsState(binding.State) || IsTerminal(binding.State))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' binds a contour to '{binding.State}', which is not one of its live states; a contour binds to a state the type has, never its terminal state.");
            }

            if (string.IsNullOrWhiteSpace(binding.Contour))
            {
                throw new DomainException($"Parcel type '{Name}' binds a contour to '{binding.State}' without naming the contour.");
            }
        }

        string? twice = ContourBindings
            .GroupBy(b => b.State, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (twice is not null)
        {
            throw new DomainException($"Parcel type '{Name}' binds more than one contour to '{twice}'; a state carries at most one contour binding.");
        }
    }

    private void ValidateGateBindings(IReadOnlySet<string> knownGates)
    {
        foreach (GateBinding? binding in GateBindings)
        {
            // Read content can carry a null element or a missing field, whatever the annotations say.
            if (binding?.From is null || binding.To is null || binding.Gate is null)
            {
                throw new DomainException($"Parcel type '{Name}' has a gate binding without a from state, a to state, or a gate; each binding names all three.");
            }

            if (!HoldsMove(binding.From, binding.To) && !IsGatedClose(binding.From, binding.To))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' binds a gate to the move from '{binding.From}' to '{binding.To}', which its move table does not hold; a gate binds only to a move the table holds or to a close from a live state.");
            }

            if (!knownGates.Contains(binding.Gate))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' binds the gate '{binding.Gate}', which is not a gate this daemon runs; a gate binding names a known gate.");
            }
        }
    }

    /// <summary>
    /// The fields and the bindings join the hashed shape only when the type has some, so adding either
    /// to the shape left the hash of a type that declares neither as it was. That holds for fields and
    /// bindings alone: a change to the lifecycle's own keys or states (the paused state of 2026-11-03,
    /// for one) changes every type's hash, and a record written before such a change is not read across it.
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
            ["paused"] = PausedState,
            ["moves"] = Moves.OrderBy(m => m.Key, StringComparer.Ordinal).ToDictionary(m => m.Key, m => m.Value),
        };
        if (Fields.Count > 0)
        {
            lifecycle["fields"] = Fields.Select(f => new { name = f.Name, kind = f.Kind, requiredToLeave = f.RequiredToLeave }).ToList();
        }

        if (ContourBindings.Count > 0)
        {
            lifecycle["contours"] = ContourBindings.Select(b => new[] { b.State, b.Contour }).ToList();
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
