using System.Text.Json.Serialization;

namespace Coanda.Core.Triggers;

/// <summary>
/// What a trigger does when an event matches it: exactly one of <see cref="CreateParcel"/>, which
/// creates a parcel and starts a flow on it, and <see cref="EventParcel"/>, which starts a flow on
/// the parcel the event names. <see cref="TriggerDefinition.Validate"/> refuses a target that holds
/// neither or both.
/// </summary>
public sealed record TriggerTarget
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CreateParcelTarget? CreateParcel { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EventParcelTarget? EventParcel { get; init; }
}
