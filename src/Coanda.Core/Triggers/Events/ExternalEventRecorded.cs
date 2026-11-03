namespace Coanda.Core.Triggers.Events;

/// <summary>Something outside the node happened in a project: a kind and named string fields, as an adapter reported it.</summary>
public sealed record ExternalEventRecorded(
    Guid EventId,
    string Project,
    string Kind,
    IReadOnlyDictionary<string, string> Fields,
    string RecordedBy,
    DateTimeOffset RecordedAt);
