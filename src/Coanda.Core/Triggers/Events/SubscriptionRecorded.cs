using Coanda.Core.Registry;

namespace Coanda.Core.Triggers.Events;

/// <summary>
/// A person subscribed, in a project, to external events matching a trigger: each match creates a
/// parcel of <see cref="ParcelType"/> titled with the event's <see cref="TitleField"/> and starts
/// <see cref="Flow"/> on it, acting as the principal that recorded the subscription.
/// </summary>
public sealed record SubscriptionRecorded(
    Guid SubscriptionId,
    string Project,
    string EventKind,
    IReadOnlyDictionary<string, string> Filters,
    string ParcelType,
    string TitleField,
    string Flow,
    string RecordedBy,
    Role RecordedByRole,
    DateTimeOffset RecordedAt);
