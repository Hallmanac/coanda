namespace Coanda.Core.Parcels;

/// <summary>
/// A relation a parcel holds to another parcel of its project: its kind (one of <see cref="RelationKinds"/>
/// when this node recorded it, and whatever text the recording node wrote when it was imported), the
/// other parcel's id, and the flow run that caused it, when it names one.
/// </summary>
public sealed record ParcelRelation(string Kind, Guid RelatedParcelId, Guid? FlowRunId);
