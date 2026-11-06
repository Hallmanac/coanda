using System.Text.Json.Serialization;
using Coanda.Core.Parcels.Events;

namespace Coanda.Core.Parcels;

/// <summary>
/// The unit of work a flow run acts on. State changes only through <c>Apply</c>; the rules live in
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
    public string Stage { get; private set; } = string.Empty;
    [JsonInclude]
    public string Title { get; private set; } = string.Empty;
    [JsonInclude]
    public string CreatedBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>What created the parcel when a person's add did not; see <see cref="ParcelCreated.Origin"/>.</summary>
    [JsonInclude]
    public string? Origin { get; private set; }
    [JsonInclude]
    public DateTimeOffset UpdatedAt { get; private set; }
    [JsonInclude]
    public string? Resolution { get; private set; }
    [JsonInclude]
    public string? CloseReason { get; private set; }
    /// <summary>
    /// The flow run that is running or paused on this parcel, as this node folds it, or null when it has none. A
    /// parcel has at most one: the first flow run attached while none was live, which only its own release clears.
    /// So the winning node keeps its own live flow run whatever order the losing node's attach and release arrive
    /// in. Nodes do not always agree on it, though: a node that held another flow run live when the winner's attach
    /// arrived ignored that attach, so once its own flow run is released it folds none while the winner's still runs.
    /// </summary>
    [JsonInclude]
    public Guid? LiveFlowRunId { get; private set; }

    /// <summary>The nodes that recorded a claim on this parcel, in the order this node stored them, none repeated.</summary>
    [JsonInclude]
    public IReadOnlyList<Guid> Claimants { get; private set; } = [];

    /// <summary>Whether any node's claim on this parcel is stored; a parcel with none is unclaimed.</summary>
    public bool IsClaimed => Claimants.Count > 0;

    /// <summary>
    /// The value of each field that has been set, by field name. A field that was never set has no
    /// entry, and a parcel recorded before fields existed reads with none.
    /// </summary>
    [JsonInclude]
    public IReadOnlyDictionary<string, FieldValue> Fields { get; private set; } = new Dictionary<string, FieldValue>(StringComparer.Ordinal);

    public bool IsClosed => Resolution is not null;

    /// <summary>The value the field holds, or null when it has never been set.</summary>
    public FieldValue? ValueOf(string field) => Fields.GetValueOrDefault(field);

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
        Stage = created.Stage;
        Title = created.Title;
        CreatedBy = created.CreatedBy;
        CreatedAt = created.CreatedAt;
        Origin = created.Origin;
        UpdatedAt = created.CreatedAt;
    }

    public void Apply(ParcelMoved moved)
    {
        Stage = moved.ToStage;
        UpdatedAt = moved.MovedAt;
    }

    public void Apply(ParcelFieldSet set)
    {
        Fields = new Dictionary<string, FieldValue>(Fields, StringComparer.Ordinal) { [set.Field] = set.Value };
        UpdatedAt = set.SetAt;
    }

    public void Apply(ParcelClosed closed)
    {
        Stage = closed.TerminalStage;
        Resolution = closed.Resolution;
        CloseReason = closed.Reason;
        UpdatedAt = closed.ClosedAt;
    }

    /// <summary>An attach while another flow run is live is another node's, which lost the parcel; it leaves the live one in place.</summary>
    public void Apply(ParcelFlowRunAttached attached)
    {
        LiveFlowRunId ??= attached.FlowRunId;
        UpdatedAt = attached.AttachedAt;
    }

    /// <summary>A release clears the live flow run only when it names it.</summary>
    public void Apply(ParcelFlowRunReleased released)
    {
        if (LiveFlowRunId == released.FlowRunId)
        {
            LiveFlowRunId = null;
        }

        UpdatedAt = released.ReleasedAt;
    }

    public void Apply(ParcelClaimed claimed)
    {
        if (!Claimants.Contains(claimed.NodeId))
        {
            Claimants = [.. Claimants, claimed.NodeId];
        }

        UpdatedAt = claimed.ClaimedAt;
    }
}
