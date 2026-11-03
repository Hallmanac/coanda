using System.Text.Json.Serialization;
using Coanda.Core.Triggers.Events;

namespace Coanda.Core.Triggers;

/// <summary>A recorded external event, one stream each. State changes only through <c>Apply</c>; the rules live in <see cref="TriggerDecider"/>.</summary>
public sealed class ExternalEvent
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public string Project { get; private set; } = string.Empty;
    [JsonInclude]
    public string Kind { get; private set; } = string.Empty;
    [JsonInclude]
    public Dictionary<string, string> Fields { get; private set; } = new(StringComparer.Ordinal);
    [JsonInclude]
    public string RecordedBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset RecordedAt { get; private set; }

    public static ExternalEvent Create(ExternalEventRecorded recorded)
    {
        var external = new ExternalEvent();
        external.Apply(recorded);
        return external;
    }

    public void Apply(ExternalEventRecorded recorded)
    {
        Id = recorded.EventId;
        Project = recorded.Project;
        Kind = recorded.Kind;
        Fields = new Dictionary<string, string>(recorded.Fields, StringComparer.Ordinal);
        RecordedBy = recorded.RecordedBy;
        RecordedAt = recorded.RecordedAt;
    }
}
