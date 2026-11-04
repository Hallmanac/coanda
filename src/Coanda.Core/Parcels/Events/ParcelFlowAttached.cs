namespace Coanda.Core.Parcels.Events;

/// <summary>
/// A flow started on the parcel and is now its live flow. Written to the parcel stream in the
/// same unit of work as the flow's start, so the parcel stream's optimistic concurrency is what
/// keeps two racing starts from both succeeding.
/// </summary>
public sealed record ParcelFlowAttached(
    Guid ParcelId,
    Guid FlowId,
    DateTimeOffset AttachedAt);
