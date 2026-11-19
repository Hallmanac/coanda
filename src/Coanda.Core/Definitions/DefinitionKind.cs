using System.Text.Json.Serialization;

namespace Coanda.Core.Definitions;

/// <summary>
/// The four customization seams, and the only kinds of definition there are. A definition's
/// content is JSON text whose shape the kind decides, so one stream shape serves all four.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DefinitionKind>))]
public enum DefinitionKind
{
    ParcelType,
    Flow,
    Agent,
    Trigger,
}
