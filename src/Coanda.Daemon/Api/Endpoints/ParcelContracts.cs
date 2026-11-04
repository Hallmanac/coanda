using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Daemon.Api.Endpoints;

public sealed record ParcelAddRequest(string Project, string Type, string Title, string? Stage);

public sealed record ParcelMoveRequest(string Project, Guid ParcelId, string To);

public sealed record ParcelCloseRequest(string Project, Guid ParcelId, string Resolution, string Reason);

/// <summary>A value for one of a parcel's fields: exactly one of its text, its items, and its reference, the one the field's kind names.</summary>
public sealed record ParcelSetFieldRequest(string Project, Guid ParcelId, string Field, FieldValue Value);

/// <summary>A field the parcel's type version declares, with the value the parcel holds in it, or null when it holds none.</summary>
public sealed record ParcelFieldView(string Name, string Kind, FieldValue? Value);

public sealed record ParcelView(
    Guid Id,
    string Project,
    string Type,
    string TypeHash,
    string Stage,
    string Title,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Resolution,
    string? CloseReason,
    Guid? LiveFlowRunId,
    IReadOnlyList<ParcelFieldView> Fields)
{
    /// <summary>
    /// The parcel as the API shows it, with every field <paramref name="type"/> declares, the version
    /// the parcel was created under. With no type to read, it shows no fields, which only a parcel whose
    /// type version the node no longer holds can come to.
    /// </summary>
    public static ParcelView From(Parcel parcel, ParcelType? type) => new(
        parcel.Id, parcel.Project, parcel.TypeName, parcel.TypeHash, parcel.Stage, parcel.Title,
        parcel.CreatedBy, parcel.CreatedAt, parcel.UpdatedAt, parcel.Resolution, parcel.CloseReason, parcel.LiveFlowRunId,
        [.. (type?.Fields ?? []).Select(f => new ParcelFieldView(f.Name, f.Kind, parcel.ValueOf(f.Name)))]);
}
