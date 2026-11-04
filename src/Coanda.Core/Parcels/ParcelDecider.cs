using Coanda.Core.Parcels.Events;
using Coanda.Core.Types;

namespace Coanda.Core.Parcels;

/// <summary>
/// The rules of a parcel's lifecycle, as pure decisions: given the type, the current parcel, and
/// an intent, either the event that records it or a <see cref="DomainException"/> quoting the rule.
/// The decider is given the gates the daemon runs; a move or a close runs every gate its type binds to it.
/// A field is set only to a non-empty value of the kind the type declares it.
/// A parcel a live flow holds (running or paused) is closed by nobody and moved only by that flow.
/// </summary>
public static class ParcelDecider
{
    public static ParcelCreated Create(
        ParcelType type, GateSet gates, string project, string title, string? entryState, string createdBy, DateTimeOffset now, string? origin = null)
    {
        type.Validate(gates.Names);

        if (string.IsNullOrWhiteSpace(project))
        {
            throw new DomainException("A parcel belongs to a project; none was given.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("A parcel needs a title.");
        }

        string state = entryState ?? type.EntryStates[0];
        if (!type.IsEntry(state))
        {
            throw new DomainException(
                $"Parcel type '{type.Name}' cannot start at '{state}'; its entry states are {string.Join(", ", type.EntryStates)}.");
        }

        return new ParcelCreated(Ids.New(), project.Trim(), type.Name, type.Hash, state, title.Trim(), createdBy, now, origin);
    }

    /// <summary>
    /// Moves the parcel. <paramref name="forFlow"/> is the flow the move is made on behalf of, or null for
    /// a move nobody's flow makes; a parcel a live flow holds is moved only on behalf of that flow.
    /// </summary>
    public static ParcelMoved Move(
        ParcelType type, GateSet gates, Parcel parcel, string toState, string movedBy, DateTimeOffset now, Guid? forFlow = null)
    {
        RequireOpen(parcel);
        RequireType(type, parcel);

        if (parcel.LiveFlowId is Guid live && live != forFlow)
        {
            throw new DomainException(
                $"Parcel {Ids.Short(parcel.Id)} is held by flow {Ids.Short(live)}, and only that flow moves it; stop flow {Ids.Short(live)} (flow.stop) before moving the parcel.");
        }

        if (type.IsTerminal(toState))
        {
            throw new DomainException($"Closing a parcel takes a resolution; use close rather than a move to '{toState}'.");
        }

        if (!type.AllowsMove(parcel.State, toState))
        {
            throw new DomainException(
                $"Parcel type '{type.Name}' does not allow a move from '{parcel.State}' to '{toState}'.");
        }

        foreach (GateBinding binding in type.GatesOn(parcel.State, toState))
        {
            RequirePasses(binding, gates, type, parcel, toState);
        }

        return new ParcelMoved(parcel.Id, parcel.State, toState, movedBy, now);
    }

    public static ParcelClosed Close(
        ParcelType type, GateSet gates, Parcel parcel, string resolution, string reason, string closedBy, DateTimeOffset now)
    {
        RequireOpen(parcel);
        RequireType(type, parcel);

        if (parcel.LiveFlowId is Guid live)
        {
            throw new DomainException(
                $"Parcel {Ids.Short(parcel.Id)} is held by flow {Ids.Short(live)}, and a parcel a live flow holds is closed by nobody; stop flow {Ids.Short(live)} (flow.stop) before closing the parcel.");
        }

        if (!type.IsResolution(resolution))
        {
            throw new DomainException(
                $"Parcel type '{type.Name}' closes with one of {string.Join(", ", type.Resolutions)}, not '{resolution}'.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Closing a parcel records why; give a reason.");
        }

        foreach (GateBinding binding in type.GatesOn(parcel.State, type.TerminalState))
        {
            RequirePasses(binding, gates, type, parcel, type.TerminalState);
        }

        return new ParcelClosed(parcel.Id, parcel.State, type.TerminalState, resolution, reason.Trim(), closedBy, now);
    }

    /// <summary>
    /// Records a value in one of the parcel's fields, replacing the value it held. The field must be
    /// one the parcel's type version declares, the value must be of the field's kind, and it must not
    /// be empty, so a field that has been set always holds a value. A closed parcel is set by nobody.
    /// </summary>
    public static ParcelFieldSet SetField(
        ParcelType type, Parcel parcel, string field, FieldValue value, string setBy, DateTimeOffset now)
    {
        if (parcel.IsClosed)
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is closed ({parcel.Resolution}); a closed parcel's fields are set by nobody.");
        }

        RequireType(type, parcel);

        FieldDeclaration declared = type.Field(field)
            ?? throw new DomainException(
                $"Parcel type '{type.Name}' declares no field '{field}'; a parcel holds values only in the fields its type declares"
                + (type.Fields.Count == 0 ? ", and it declares none." : $": {string.Join(", ", type.Fields.Select(f => f.Name))}."));

        // A request read from the wire can carry no value at all, whatever the annotations say.
        if (value is null)
        {
            throw new DomainException($"No value was given for field '{declared.Name}'; a field is set to a value of its own kind.");
        }

        if (!string.Equals(value.Kind, declared.Kind, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Field '{declared.Name}' of a '{type.Name}' holds a {Describe(declared.Kind)}, and the value given is {(value.Kind is null ? "not one kind of value" : $"a {Describe(value.Kind)}")}; a field takes only a value of its own kind.");
        }

        if (IsEmpty(value))
        {
            throw new DomainException(
                $"The value given for field '{declared.Name}' is empty; a field takes no empty value: no text that is empty or only white space, no list without items or with an empty item, and no reference without an identifier.");
        }

        FieldValue recorded = value.Reference is { } reference && string.IsNullOrWhiteSpace(reference.Url)
            ? value with { Reference = reference with { Url = null } }
            : value;
        return new ParcelFieldSet(parcel.Id, declared.Name, recorded, setBy, now);
    }

    private static bool IsEmpty(FieldValue value) => value switch
    {
        { Text: { } text } => string.IsNullOrWhiteSpace(text),
        { Items: { } items } => items.Count == 0 || items.Any(string.IsNullOrWhiteSpace),
        { Reference: { } reference } => string.IsNullOrWhiteSpace(reference.Id),
        _ => true,
    };

    private static string Describe(string kind) => kind switch
    {
        FieldKinds.TextList => "list of text",
        _ => kind,
    };

    /// <summary>
    /// Runs the gate the binding names on the move the binding stands for, which is the move being
    /// made except out of the paused state, where it is the bound move into the same target.
    /// </summary>
    private static void RequirePasses(GateBinding binding, GateSet gates, ParcelType type, Parcel parcel, string toState)
    {
        IGate gate = gates.Find(binding.Gate)
            ?? throw new DomainException(
                $"The gate '{binding.Gate}' bound to the move to '{toState}' is not a gate this daemon runs, so the move is refused.");

        if (gate.Check(parcel, type, binding.From, binding.To) is GateVerdict.Failed failed)
        {
            throw new DomainException($"The gate '{binding.Gate}' refused the move from '{parcel.State}' to '{toState}': {failed.Reason}");
        }
    }

    private static void RequireOpen(Parcel parcel)
    {
        if (parcel.IsClosed)
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is closed ({parcel.Resolution}); nothing moves it.");
        }
    }

    private static void RequireType(ParcelType type, Parcel parcel)
    {
        if (!string.Equals(type.Name, parcel.TypeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is a '{parcel.TypeName}', not a '{type.Name}'.");
        }

        if (!string.Equals(type.Hash, parcel.TypeHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Parcel {Ids.Short(parcel.Id)} was created under '{parcel.TypeName}@{parcel.TypeHash}', not '{type.Name}@{type.Hash}'.");
        }
    }
}
