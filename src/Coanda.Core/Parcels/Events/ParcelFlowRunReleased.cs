namespace Coanda.Core.Parcels.Events;

/// <summary>The parcel's live flow run ended or was stopped, so the parcel has none and a new flow run may start.</summary>
public sealed record ParcelFlowRunReleased(
    Guid ParcelId,
    Guid FlowRunId,
    DateTimeOffset ReleasedAt);
