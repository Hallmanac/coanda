using System.Text.Json.Serialization;

namespace Coanda.Core.Definitions;

/// <summary>
/// Where a definition applies, nearest wins by name: a project's own, the owner's across every
/// project they hold, or the one carried by the binary.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DefinitionScope>))]
public enum DefinitionScope
{
    Project,
    Owner,
    Shipped,
}
