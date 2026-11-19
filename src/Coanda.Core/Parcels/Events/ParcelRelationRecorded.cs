namespace Coanda.Core.Parcels.Events;

/// <summary>
/// The parcel recorded a relation of <see cref="Kind"/> to <see cref="RelatedParcelId"/>, another parcel
/// of its project, naming <see cref="FlowRunId"/> when a flow run caused it. It is recorded on the stream
/// of the parcel that holds it: the derived parcel, or the blocked one. The kind is its text (see
/// <see cref="RelationKinds"/>), so a later kind is a new value and not a new shape.
/// </summary>
public sealed record ParcelRelationRecorded(
    Guid ParcelId,
    string Kind,
    Guid RelatedParcelId,
    Guid? FlowRunId,
    string RecordedBy,
    DateTimeOffset RecordedAt);
