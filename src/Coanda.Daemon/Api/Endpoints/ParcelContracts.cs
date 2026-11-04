using Coanda.Core.Parcels;

namespace Coanda.Daemon.Api.Endpoints;

public sealed record ParcelAddRequest(string Project, string Type, string Title, string? State);

public sealed record ParcelMoveRequest(string Project, Guid ParcelId, string To);

public sealed record ParcelCloseRequest(string Project, Guid ParcelId, string Resolution, string Reason);

public sealed record ParcelView(
    Guid Id,
    string Project,
    string Type,
    string TypeHash,
    string State,
    string Title,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Resolution,
    string? CloseReason,
    Guid? LiveFlowId)
{
    public static ParcelView From(Parcel parcel) => new(
        parcel.Id, parcel.Project, parcel.TypeName, parcel.TypeHash, parcel.State, parcel.Title,
        parcel.CreatedBy, parcel.CreatedAt, parcel.UpdatedAt, parcel.Resolution, parcel.CloseReason, parcel.LiveFlowId);
}
