namespace Coanda.Core.Triggers;

/// <summary>
/// An event as a trigger reads it: a kind and named string fields. A parcel's creation and every
/// move read as <see cref="ParcelEnteredKind"/> with the fields <c>type</c> and <c>state</c>; a
/// flow run's end reads as <see cref="FlowRunEndedKind"/> with the fields <c>contour</c>, <c>outcome</c>,
/// and <c>type</c>, the type of the flow run's parcel.
/// </summary>
public sealed record TriggerEvent(string Kind, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>The kind a parcel's creation and every move read as; no external event or subscription may use it.</summary>
    public const string ParcelEnteredKind = "parcel-entered";

    /// <summary>The kind a flow run's end reads as; no external event or subscription may use it.</summary>
    public const string FlowRunEndedKind = "flow-run-ended";

    public const string TypeField = "type";
    public const string StateField = "state";
    public const string ContourField = "contour";
    public const string OutcomeField = "outcome";

    public static TriggerEvent ParcelEntered(string typeName, string state) => new(
        ParcelEnteredKind,
        new Dictionary<string, string>(StringComparer.Ordinal) { [TypeField] = typeName, [StateField] = state });

    /// <summary>
    /// A flow run's end. Its <c>contour</c> field carries the contour's name as <see cref="ContourKey"/> folds it,
    /// because contour names compare without regard to case.
    /// </summary>
    public static TriggerEvent FlowRunEnded(string contourName, string outcome, string typeName) => new(
        FlowRunEndedKind,
        new Dictionary<string, string>(StringComparer.Ordinal) { [ContourField] = ContourKey(contourName), [OutcomeField] = outcome, [TypeField] = typeName });

    /// <summary>
    /// A contour's name folded to one case, so that the exact equality a trigger compares with finds two
    /// spellings of the same contour equal, as every other comparison of contour names does.
    /// </summary>
    public static string ContourKey(string contourName) => contourName.ToUpperInvariant();
}
