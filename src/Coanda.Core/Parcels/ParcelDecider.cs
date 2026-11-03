using Coanda.Core.Parcels.Events;
using Coanda.Core.Types;

namespace Coanda.Core.Parcels;

/// <summary>
/// The rules of a parcel's lifecycle, as pure decisions: given the type, the current parcel, and
/// an intent, either the event that records it or a <see cref="DomainException"/> quoting the rule.
/// </summary>
public static class ParcelDecider
{
    public static ParcelCreated Create(
        ParcelType type, string project, string title, string? entryState, string createdBy, DateTimeOffset now)
    {
        type.Validate();

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

        return new ParcelCreated(Ids.New(), project.Trim(), type.Name, type.Hash, state, title.Trim(), createdBy, now);
    }

    public static ParcelMoved Move(ParcelType type, Parcel parcel, string toState, string movedBy, DateTimeOffset now)
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

        return new ParcelMoved(parcel.Id, parcel.State, toState, movedBy, now);
    }

    public static ParcelClosed Close(
        ParcelType type, Parcel parcel, string resolution, string reason, string closedBy, DateTimeOffset now)
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

        return new ParcelClosed(parcel.Id, parcel.State, type.TerminalState, resolution, reason.Trim(), closedBy, now);
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
        if (!string.Equals(type.Name, parcel.TypeName, StringComparison.Ordinal))
        {
            throw new DomainException($"Parcel {Ids.Short(parcel.Id)} is a '{parcel.TypeName}', not a '{type.Name}'.");
        }
    }
}
