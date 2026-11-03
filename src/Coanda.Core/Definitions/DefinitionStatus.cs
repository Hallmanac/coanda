using System.Text.Json.Serialization;

namespace Coanda.Core.Definitions;

/// <summary>
/// The kernel-hardwired status of a definition. Only a published definition governs work. This is
/// not a parcel lifecycle and is not called one.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DefinitionStatus>))]
public enum DefinitionStatus
{
    Draft,
    Published,
    Retired,
}
