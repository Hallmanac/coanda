namespace Coanda.Core.Parcels.Events;

/// <summary>The parcel's live flow ended or was stopped, so the parcel has none and a new flow may start.</summary>
public sealed record ParcelFlowReleased(
    Guid ParcelId,
    Guid FlowId,
    DateTimeOffset ReleasedAt);
