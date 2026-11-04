using System.Text.Json.Serialization;
using Coanda.Core.Types;

namespace Coanda.Core.Parcels;

/// <summary>
/// The value a parcel holds in one field: exactly one of <see cref="Text"/>, <see cref="Items"/>, and
/// <see cref="Reference"/> is set, the one the field's kind names. Which one is set is checked against
/// the declaration when the value is recorded, never assumed.
/// </summary>
public sealed record FieldValue
{
    public string? Text { get; init; }
    public IReadOnlyList<string>? Items { get; init; }
    public FieldReference? Reference { get; init; }

    public static FieldValue OfText(string text) => new() { Text = text };

    public static FieldValue OfItems(params IReadOnlyList<string> items) => new() { Items = items };

    public static FieldValue OfReference(string id, string? url = null) => new() { Reference = new FieldReference(id, url) };

    /// <summary>The kind the value is shaped as, or null when it sets none or more than one shape; derived, so never written.</summary>
    [JsonIgnore]
    public string? Kind => (Text, Items, Reference) switch
    {
        (not null, null, null) => FieldKinds.Text,
        (null, not null, null) => FieldKinds.TextList,
        (null, null, not null) => FieldKinds.Reference,
        _ => null,
    };
}
