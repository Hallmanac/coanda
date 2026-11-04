using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;

namespace Coanda.Core.Types;

/// <summary>
/// The definition of a kind of parcel: its lifecycle (stages, allowed moves, entry stages, the
/// one terminal stage and its resolutions), the paused stage reachable from any live stage, its
/// fields (each named, of a kind, and required to leave the stages it names), and its bindings: a
/// flow to start when a parcel enters a stage, and a gate that must pass before a move. A type is
/// data; the types the binary ships and a user's type are the same shape.
/// </summary>
public sealed record ParcelType
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Stages { get; init; }
    public required IReadOnlyList<string> EntryStages { get; init; }
    public required string TerminalStage { get; init; }
    public required IReadOnlyList<string> Resolutions { get; init; }
    public required string PausedStage { get; init; }
    /// <summary>Allowed moves between live stages, as from-stage to the set of to-stages.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Moves { get; init; }

    /// <summary>The flow started on a parcel when it enters a stage; at most one per stage.</summary>
    public IReadOnlyList<FlowBinding> FlowBindings { get; init; } = [];

    /// <summary>The gates that must pass before a move the move table holds, or before a close from a live stage.</summary>
    public IReadOnlyList<GateBinding> GateBindings { get; init; } = [];

    /// <summary>The fields a parcel of the type holds values in, in the order the type declares them.</summary>
    public IReadOnlyList<FieldDeclaration> Fields { get; init; } = [];

    /// <summary>Content hash of the definition, recorded on every parcel created under it; computed, so never written into content.</summary>
    [JsonIgnore]
    public string Hash => ComputeHash();

    public bool IsStage(string stage) => Stages.Contains(stage, StringComparer.Ordinal);

    public bool IsTerminal(string stage) => string.Equals(stage, TerminalStage, StringComparison.Ordinal);

    public bool IsPaused(string stage) => string.Equals(stage, PausedStage, StringComparison.Ordinal);

    public bool IsEntry(string stage) => EntryStages.Contains(stage, StringComparer.Ordinal);

    public bool IsResolution(string resolution) => Resolutions.Contains(resolution, StringComparer.Ordinal);

    /// <summary>The field the type declares by that name, or null when it declares none.</summary>
    public FieldDeclaration? Field(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The gates a move from <paramref name="from"/> to <paramref name="to"/> must pass. A move out of
    /// the paused stage passes every gate bound to a move into the target, so pausing bypasses none.
    /// </summary>
    public IReadOnlyList<GateBinding> GatesOn(string from, string to) =>
    [
        .. GateBindings.Where(b => string.Equals(b.To, to, StringComparison.Ordinal)
            && (IsPaused(from) || string.Equals(b.From, from, StringComparison.Ordinal))),
    ];

    /// <summary>
    /// The fields, in declaration order, a parcel must hold values in to enter the live stage
    /// <paramref name="stage"/>: those that every route the move table holds from an entry stage to it
    /// requires, before reaching it, to leave some stage on the way. An entry stage requires nothing to
    /// enter, and neither does a stage no route reaches, nor the paused or terminal stage. A move the
    /// table holds keeps this by itself, since it checks what its from stage requires to leave and a set
    /// field cannot be cleared; a resume out of paused is the one way around that, so it checks this in
    /// addition to what each bound move into the stage checks.
    /// </summary>
    public IReadOnlyList<string> RequiredToEnter(string stage) => RequiredToEnter(RequiredToEnterByStage(), stage);

    private IReadOnlyList<string> RequiredToEnter(Dictionary<string, HashSet<string>> byStage, string stage) =>
        byStage.TryGetValue(stage, out HashSet<string>? required)
            ? [.. Fields.Select(f => f.Name).Where(required.Contains)]
            : [];

    /// <summary>
    /// A must-hold dataflow over the move table. An entry stage starts and stays empty; every other
    /// stage a route reaches starts at every declared field and is narrowed, until nothing changes, to
    /// the intersection over its reached predecessors of what each requires to enter plus what it
    /// requires to leave. Intersecting over routes rather than taking a union over predecessors keeps
    /// a move back (review to build, say) from demanding what only the later stage needs.
    /// </summary>
    private Dictionary<string, HashSet<string>> RequiredToEnterByStage()
    {
        List<(string From, string To)> moves =
        [
            .. Moves.Where(m => IsLive(m.Key))
                .SelectMany(m => m.Value.Where(IsLive).Select(to => (From: m.Key, To: to))),
        ];

        var reached = new HashSet<string>(EntryStages.Where(IsLive), StringComparer.Ordinal);
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
            foreach (string stage in reached.Where(s => !IsEntry(s)))
            {
                HashSet<string> narrowed = new(all, StringComparer.Ordinal);
                foreach ((string from, _) in moves.Where(m => string.Equals(m.To, stage, StringComparison.Ordinal) && reached.Contains(m.From)))
                {
                    narrowed.IntersectWith(required[from].Concat(Fields.Where(f => f.IsRequiredToLeave(from)).Select(f => f.Name)));
                }

                if (!narrowed.SetEquals(required[stage]))
                {
                    required[stage] = narrowed;
                    changed = true;
                }
            }
        }

        return required;
    }

    private bool IsLive(string stage) => IsStage(stage) && !IsTerminal(stage) && !IsPaused(stage);

    /// <summary>Whether the move table itself holds a move, as opposed to the pause, resume, and close every type allows.</summary>
    public bool HoldsMove(string from, string to) =>
        Moves.TryGetValue(from, out IReadOnlyList<string>? targets) && targets.Contains(to, StringComparer.Ordinal);

    /// <summary>Whether a gate binding names a close: from a live stage other than paused, to the terminal stage.</summary>
    private bool IsGatedClose(string from, string to) =>
        IsTerminal(to) && IsStage(from) && !IsTerminal(from) && !IsPaused(from);

    /// <summary>
    /// Whether the lifecycle allows a move. Pausing is allowed from any live stage; resuming from
    /// paused is allowed to any live stage; closing is allowed from any live or paused stage and is
    /// expressed by the terminal stage; nothing leaves the terminal stage.
    /// </summary>
    public bool AllowsMove(string from, string to)
    {
        if (!IsStage(from) || !IsStage(to) || IsTerminal(from) || string.Equals(from, to, StringComparison.Ordinal))
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
    /// other is refused, the way a flow is validated against the action names.
    /// </summary>
    public void Validate(IReadOnlySet<string> knownGates)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainException("A parcel type needs a name.");
        }

        if (Stages.Distinct(StringComparer.Ordinal).Count() != Stages.Count)
        {
            throw new DomainException($"Parcel type '{Name}' lists a stage twice.");
        }

        if (!IsStage(TerminalStage))
        {
            throw new DomainException($"Parcel type '{Name}' names a terminal stage '{TerminalStage}' that is not one of its stages.");
        }

        if (!IsStage(PausedStage) || IsTerminal(PausedStage))
        {
            throw new DomainException($"Parcel type '{Name}' needs a paused stage distinct from its terminal stage.");
        }

        if (EntryStages.Count == 0 || EntryStages.Any(s => !IsStage(s) || IsTerminal(s) || IsPaused(s)))
        {
            throw new DomainException($"Parcel type '{Name}' needs at least one entry stage, each a live stage.");
        }

        if (Resolutions.Count == 0)
        {
            throw new DomainException($"Parcel type '{Name}' needs at least one resolution for its terminal stage.");
        }

        foreach ((string from, IReadOnlyList<string> targets) in Moves)
        {
            if (!IsStage(from) || targets.Any(t => !IsStage(t)))
            {
                throw new DomainException($"Parcel type '{Name}' has a move naming a stage it does not have.");
            }
        }

        ValidateFields();
        ValidateFlowBindings();
        ValidateGateBindings(knownGates);
        ValidateFieldEnforcement();
    }

    /// <summary>
    /// A field's declarations bind only where the required-fields gate runs, so a declaration the gate
    /// bindings leave unenforced is refused: every move the table holds out of a stage a field is
    /// required to leave is bound to the gate, and every stage some field is required to enter has a
    /// move into it bound to the gate, which is what a resume into that stage runs. An entry in the
    /// table into the paused or terminal stage is a pause or a close, which no empty field refuses, so
    /// the first rule asks nothing of it. A third rule closes the way around the second: a stage no
    /// route from an entry stage reaches requires nothing to enter, so a resume into it checks nothing,
    /// and a move out of it into a stage some field is required to enter is refused, since it would
    /// carry a parcel there without what every route into that stage requires.
    /// </summary>
    private void ValidateFieldEnforcement()
    {
        bool Bound(string from, string to) => GateBindings.Any(b =>
            string.Equals(b.From, from, StringComparison.Ordinal)
            && string.Equals(b.To, to, StringComparison.Ordinal)
            && string.Equals(b.Gate, RequiredFieldsGate.GateName, StringComparison.Ordinal));

        foreach (FieldDeclaration field in Fields)
        {
            foreach (string stage in field.RequiredToLeave)
            {
                IReadOnlyList<string> targets = Moves.GetValueOrDefault(stage) ?? [];
                string? unbound = targets.FirstOrDefault(to => IsLive(to) && !Bound(stage, to));
                if (unbound is not null)
                {
                    throw new DomainException(
                        $"Field '{field.Name}' of parcel type '{Name}' is required to leave '{stage}', and the move from '{stage}' to '{unbound}' is not bound to the '{RequiredFieldsGate.GateName}' gate; every move the table holds out of a stage a field is required to leave is bound to that gate.");
                }
            }
        }

        Dictionary<string, HashSet<string>> requiredByStage = RequiredToEnterByStage();
        foreach (string stage in Stages.Where(IsLive))
        {
            IReadOnlyList<string> required = RequiredToEnter(requiredByStage, stage);
            bool guarded = Moves.Any(m => m.Value.Contains(stage, StringComparer.Ordinal) && Bound(m.Key, stage));
            if (required.Count > 0 && !guarded)
            {
                throw new DomainException(
                    $"Stage '{stage}' of parcel type '{Name}' requires {string.Join(", ", required.Select(f => $"'{f}'"))} to enter, and no move the table holds into it is bound to the '{RequiredFieldsGate.GateName}' gate, so a resume into it would check nothing; a stage some field is required to enter has a move into it bound to that gate.");
            }
        }

        foreach ((string from, IReadOnlyList<string> targets) in Moves.Where(m => IsLive(m.Key) && !requiredByStage.ContainsKey(m.Key)))
        {
            foreach (string to in targets.Where(IsLive))
            {
                IReadOnlyList<string> required = RequiredToEnter(requiredByStage, to);
                if (required.Count > 0)
                {
                    throw new DomainException(
                        $"Stage '{to}' of parcel type '{Name}' requires {string.Join(", ", required.Select(f => $"'{f}'"))} to enter, and the move the table holds into it from '{from}', a stage no route from an entry stage reaches, would carry a parcel resumed into '{from}' there without them; a move out of a stage no route reaches goes only into a stage that requires nothing to enter.");
                }
            }
        }
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

            if (!Flow.IsPlainName(field.Name))
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

            foreach (string? stage in field.RequiredToLeave)
            {
                if (stage is null || !IsStage(stage) || IsTerminal(stage) || IsPaused(stage))
                {
                    throw new DomainException(
                        $"Field '{field.Name}' of parcel type '{Name}' is required to leave '{stage}', which is not one of its live stages; a field is required to leave only a stage the type has, never its terminal or paused stage.");
                }
            }
        }
    }

    private void ValidateFlowBindings()
    {
        foreach (FlowBinding? binding in FlowBindings)
        {
            // Read content can carry a null element or a missing field, whatever the annotations say.
            if (binding?.Stage is null || binding.Flow is null)
            {
                throw new DomainException($"Parcel type '{Name}' has a flow binding without a stage or a flow; each binding names both.");
            }

            if (!IsStage(binding.Stage) || IsTerminal(binding.Stage))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' binds a flow to '{binding.Stage}', which is not one of its live stages; a flow binds to a stage the type has, never its terminal stage.");
            }

            if (string.IsNullOrWhiteSpace(binding.Flow))
            {
                throw new DomainException($"Parcel type '{Name}' binds a flow to '{binding.Stage}' without naming the flow.");
            }
        }

        string? twice = FlowBindings
            .GroupBy(b => b.Stage, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (twice is not null)
        {
            throw new DomainException($"Parcel type '{Name}' binds more than one flow to '{twice}'; a stage carries at most one flow binding.");
        }
    }

    private void ValidateGateBindings(IReadOnlySet<string> knownGates)
    {
        foreach (GateBinding? binding in GateBindings)
        {
            // Read content can carry a null element or a missing field, whatever the annotations say.
            if (binding?.From is null || binding.To is null || binding.Gate is null)
            {
                throw new DomainException($"Parcel type '{Name}' has a gate binding without a from stage, a to stage, or a gate; each binding names all three.");
            }

            if (!HoldsMove(binding.From, binding.To) && !IsGatedClose(binding.From, binding.To))
            {
                throw new DomainException(
                    $"Parcel type '{Name}' binds a gate to the move from '{binding.From}' to '{binding.To}', which its move table does not hold; a gate binds only to a move the table holds or to a close from a live stage.");
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
    /// bindings alone: a change to the lifecycle's own keys or stages (the paused stage of 2026-11-03,
    /// for one) changes every type's hash, and a record written before such a change is not read across it.
    /// </summary>
    private string ComputeHash()
    {
        var lifecycle = new Dictionary<string, object>
        {
            ["name"] = Name,
            ["stages"] = Stages,
            ["entry"] = EntryStages,
            ["terminal"] = TerminalStage,
            ["resolutions"] = Resolutions,
            ["paused"] = PausedStage,
            ["moves"] = Moves.OrderBy(m => m.Key, StringComparer.Ordinal).ToDictionary(m => m.Key, m => m.Value),
        };
        if (Fields.Count > 0)
        {
            lifecycle["fields"] = Fields.Select(f => new { name = f.Name, kind = f.Kind, requiredToLeave = f.RequiredToLeave }).ToList();
        }

        if (FlowBindings.Count > 0)
        {
            lifecycle["flows"] = FlowBindings.Select(b => new[] { b.Stage, b.Flow }).ToList();
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
        text.Append(type.Name).Append(": ").AppendJoin(", ", type.Stages);
        return text.ToString();
    }
}
