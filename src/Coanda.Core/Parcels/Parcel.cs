using System.Text.Json.Serialization;
using Coanda.Core.Parcels.Events;

namespace Coanda.Core.Parcels;

/// <summary>
/// The unit of work a flow acts on. State changes only through <c>Apply</c>; the rules live in
/// <see cref="ParcelDecider"/>. The store snapshots this class inline, so it is also the document
/// a list or show query reads; the private setters carry <c>JsonInclude</c> so the snapshot
/// round-trips through System.Text.Json without opening them up.
/// </summary>
public sealed class Parcel
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public string Project { get; private set; } = string.Empty;
    [JsonInclude]
    public string TypeName { get; private set; } = string.Empty;
    [JsonInclude]
    public string TypeHash { get; private set; } = string.Empty;
    [JsonInclude]
    public string State { get; private set; } = string.Empty;
    [JsonInclude]
    public string Title { get; private set; } = string.Empty;
    [JsonInclude]
    public string CreatedBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset CreatedAt { get; private set; }
    [JsonInclude]
    public DateTimeOffset UpdatedAt { get; private set; }
    [JsonInclude]
    public string? Resolution { get; private set; }
    [JsonInclude]
    public string? CloseReason { get; private set; }

    public bool IsClosed => Resolution is not null;

    public static Parcel Create(ParcelCreated created)
    {
        var parcel = new Parcel();
        parcel.Apply(created);
        return parcel;
    }

    public void Apply(ParcelCreated created)
    {
        Id = created.ParcelId;
        Project = created.Project;
        TypeName = created.TypeName;
        TypeHash = created.TypeHash;
        State = created.State;
        Title = created.Title;
        CreatedBy = created.CreatedBy;
        CreatedAt = created.CreatedAt;
        UpdatedAt = created.CreatedAt;
    }

    public void Apply(ParcelMoved moved)
    {
        State = moved.ToState;
        UpdatedAt = moved.MovedAt;
    }

    public void Apply(ParcelClosed closed)
    {
        State = closed.TerminalState;
        Resolution = closed.Resolution;
        CloseReason = closed.Reason;
        UpdatedAt = closed.ClosedAt;
    }
}
