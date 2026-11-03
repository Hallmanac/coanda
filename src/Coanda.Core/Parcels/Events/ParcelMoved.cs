namespace Coanda.Core.Parcels.Events;

public sealed record ParcelMoved(
    Guid ParcelId,
    string FromState,
    string ToState,
    string MovedBy,
    DateTimeOffset MovedAt);
