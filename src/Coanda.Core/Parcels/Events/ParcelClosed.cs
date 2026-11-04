namespace Coanda.Core.Parcels.Events;

public sealed record ParcelClosed(
    Guid ParcelId,
    string FromStage,
    string TerminalStage,
    string Resolution,
    string Reason,
    string ClosedBy,
    DateTimeOffset ClosedAt);
