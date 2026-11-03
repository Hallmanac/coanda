namespace Coanda.Core.Triggers;

/// <summary>
/// An event as a trigger reads it: a kind and named string fields. A parcel's creation and every
/// move read as <see cref="ParcelEnteredKind"/> with the fields <c>type</c> and <c>state</c>.
/// </summary>
public sealed record TriggerEvent(string Kind, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>The kind a parcel's creation and every move read as; no external event or subscription may use it.</summary>
    public const string ParcelEnteredKind = "parcel-entered";

    public const string TypeField = "type";
    public const string StateField = "state";

    public static TriggerEvent ParcelEntered(string typeName, string state) => new(
        ParcelEnteredKind,
        new Dictionary<string, string>(StringComparer.Ordinal) { [TypeField] = typeName, [StateField] = state });
}
