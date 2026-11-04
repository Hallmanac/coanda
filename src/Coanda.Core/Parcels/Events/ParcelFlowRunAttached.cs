namespace Coanda.Core.Parcels.Events;

/// <summary>
/// A flow run started on the parcel and is now its live flow run. Written to the parcel stream in the
/// same unit of work as the flow run's start, so the parcel stream's optimistic concurrency is what
/// keeps two racing starts from both succeeding.
/// </summary>
public sealed record ParcelFlowRunAttached(
    Guid ParcelId,
    Guid FlowRunId,
    DateTimeOffset AttachedAt);
