namespace Coanda.Core.Parcels.Events;

public sealed record ParcelMoved(
    Guid ParcelId,
    string FromStage,
    string ToStage,
    string MovedBy,
    DateTimeOffset MovedAt);
