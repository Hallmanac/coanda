using System.Text.Json.Serialization;
using Coanda.Core.Registry;
using Coanda.Core.Triggers.Events;

namespace Coanda.Core.Triggers;

/// <summary>
/// A person's trigger in a project: the external events it matches, the parcel it creates for each,
/// and the flow it starts there. A record a person holds, not a definition: it has no draft,
/// publish, scope, or fork. State changes only through <c>Apply</c>; the rules live in
/// <see cref="TriggerDecider"/>. The store snapshots this class inline, so it is also the document
/// the trigger loop reads.
/// </summary>
public sealed class Subscription
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public string Project { get; private set; } = string.Empty;
    [JsonInclude]
    public string EventKind { get; private set; } = string.Empty;
    [JsonInclude]
    public Dictionary<string, string> Filters { get; private set; } = new(StringComparer.Ordinal);
    [JsonInclude]
    public string ParcelType { get; private set; } = string.Empty;
    [JsonInclude]
    public string TitleField { get; private set; } = string.Empty;
    [JsonInclude]
    public string Flow { get; private set; } = string.Empty;
    [JsonInclude]
    public string RecordedBy { get; private set; } = string.Empty;
    [JsonInclude]
    public Role RecordedByRole { get; private set; }
    [JsonInclude]
    public DateTimeOffset RecordedAt { get; private set; }

    [JsonIgnore]
    public Trigger Trigger => new(EventKind, Filters);

    /// <summary>The principal the subscription acts as: the one that recorded it.</summary>
    [JsonIgnore]
    public Principal Principal => new(RecordedBy, RecordedByRole);

    public static Subscription Create(SubscriptionRecorded recorded)
    {
        var subscription = new Subscription();
        subscription.Apply(recorded);
        return subscription;
    }

    public void Apply(SubscriptionRecorded recorded)
    {
        Id = recorded.SubscriptionId;
        Project = recorded.Project;
        EventKind = recorded.EventKind;
        Filters = new Dictionary<string, string>(recorded.Filters, StringComparer.Ordinal);
        ParcelType = recorded.ParcelType;
        TitleField = recorded.TitleField;
        Flow = recorded.Flow;
        RecordedBy = recorded.RecordedBy;
        RecordedByRole = recorded.RecordedByRole;
        RecordedAt = recorded.RecordedAt;
    }

    /// <summary>What a parcel this subscription created for an event records as its origin, so the event creates it once.</summary>
    public string OriginFor(Guid eventId) => $"subscription:{Id}:event:{eventId}";
}
