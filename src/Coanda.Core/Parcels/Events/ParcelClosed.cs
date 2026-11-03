namespace Coanda.Core.Parcels.Events;

public sealed record ParcelClosed(
    Guid ParcelId,
    string FromState,
    string TerminalState,
    string Resolution,
    string Reason,
    string ClosedBy,
    DateTimeOffset ClosedAt);
