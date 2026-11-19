namespace Coanda.Core.Parcels;

/// <summary>
/// The parcel a new parcel is derived from, and the flow run that caused it when one did, recorded as a
/// derived-from relation in the save that creates the new parcel.
/// </summary>
public sealed record DerivedFrom(Guid ParcelId, Guid? FlowRunId = null);
