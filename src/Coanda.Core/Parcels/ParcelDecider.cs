using Coanda.Core.Parcels.Events;
using Coanda.Core.Types;

namespace Coanda.Core.Parcels;

/// <summary>
/// The rules of a parcel's lifecycle, as pure decisions: given the type, the current parcel, and
/// an intent, either the event that records it or a <see cref="DomainException"/> quoting the rule.
/// The decider is given the gates the daemon runs; a move or a close runs every gate its type binds to it.
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

    public static ParcelMoved Move(ParcelType type, GateSet gates, Parcel parcel, string toState, string movedBy, DateTimeOffset now)
    {
        RequireOpen(parcel);
        RequireType(type, parcel);

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
            RequirePasses(binding, gates, parcel, toState);
        }

        return new ParcelMoved(parcel.Id, parcel.State, toState, movedBy, now);
    }

    public static ParcelClosed Close(
        ParcelType type, GateSet gates, Parcel parcel, string resolution, string reason, string closedBy, DateTimeOffset now)
    {
        RequireOpen(parcel);
        RequireType(type, parcel);

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
            RequirePasses(binding, gates, parcel, type.TerminalState);
        }

        return new ParcelClosed(parcel.Id, parcel.State, type.TerminalState, resolution, reason.Trim(), closedBy, now);
    }

    private static void RequirePasses(GateBinding binding, GateSet gates, Parcel parcel, string toState)
    {
        IGate gate = gates.Find(binding.Gate)
            ?? throw new DomainException(
                $"The gate '{binding.Gate}' bound to the move to '{toState}' is not a gate this daemon runs, so the move is refused.");

        if (gate.Check(parcel, parcel.State, toState) is GateVerdict.Failed failed)
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
