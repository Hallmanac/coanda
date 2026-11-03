namespace Coanda.Core.Triggers;

/// <summary>
/// An event as a trigger reads it: a kind and named string fields. A parcel's creation and every
/// move read as <see cref="ParcelEnteredKind"/> with the fields <c>type</c> and <c>state</c>; a
/// flight's end reads as <see cref="FlightEndedKind"/> with the fields <c>flow</c>, <c>outcome</c>,
/// and <c>type</c>, the type of the flight's parcel.
/// </summary>
public sealed record TriggerEvent(string Kind, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>The kind a parcel's creation and every move read as; no external event or subscription may use it.</summary>
    public const string ParcelEnteredKind = "parcel-entered";

    /// <summary>The kind a flight's end reads as; no external event or subscription may use it.</summary>
    public const string FlightEndedKind = "flight-ended";

    public const string TypeField = "type";
    public const string StateField = "state";
    public const string FlowField = "flow";
    public const string OutcomeField = "outcome";

    public static TriggerEvent ParcelEntered(string typeName, string state) => new(
        ParcelEnteredKind,
        new Dictionary<string, string>(StringComparer.Ordinal) { [TypeField] = typeName, [StateField] = state });

    public static TriggerEvent FlightEnded(string flowName, string outcome, string typeName) => new(
        FlightEndedKind,
        new Dictionary<string, string>(StringComparer.Ordinal) { [FlowField] = flowName, [OutcomeField] = outcome, [TypeField] = typeName });
}
