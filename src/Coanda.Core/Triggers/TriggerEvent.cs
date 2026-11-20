namespace Coanda.Core.Triggers;

/// <summary>
/// An event as a trigger reads it: a kind and named string fields. A parcel's creation and every
/// move read as <see cref="ParcelEnteredKind"/> with the fields <c>type</c> and <c>stage</c>; a
/// flow run's end reads as <see cref="FlowRunEndedKind"/> with the fields <c>flow</c>, <c>outcome</c>,
/// <c>type</c>, the type of the flow run's parcel, and <c>title</c>, that parcel's title; and a pause of a
/// flow run this node started reads as <see cref="AttentionKind"/> with the fields <c>flow</c>, <c>step</c>,
/// <c>type</c>, and <c>title</c>.
/// </summary>
public sealed record TriggerEvent(string Kind, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>The kind a parcel's creation and every move read as; no external event or subscription may use it.</summary>
    public const string ParcelEnteredKind = "parcel-entered";

    /// <summary>The kind a flow run's end reads as; no external event or subscription may use it.</summary>
    public const string FlowRunEndedKind = "flow-run-ended";

    /// <summary>
    /// The kind a pause of a flow run this node started reads as, other than a wait-for-external-event step's own
    /// pause, which an external event resumes; no external event may use it.
    /// </summary>
    public const string AttentionKind = "attention";

    public const string TypeField = "type";
    public const string StageField = "stage";
    public const string FlowField = "flow";
    public const string OutcomeField = "outcome";
    public const string TitleField = "title";
    public const string StepField = "step";

    public static TriggerEvent ParcelEntered(string typeName, string stage) => new(
        ParcelEnteredKind,
        new Dictionary<string, string>(StringComparer.Ordinal) { [TypeField] = typeName, [StageField] = stage });

    /// <summary>
    /// A flow run's end. Its <c>flow</c> field carries the flow's name as <see cref="FlowKey"/> folds it,
    /// because flow names compare without regard to case. Its <c>title</c> field carries the title of the
    /// flow run's parcel, which a trigger on the end may title the parcel it creates with.
    /// </summary>
    public static TriggerEvent FlowRunEnded(string flowName, string outcome, string typeName, string title) => new(
        FlowRunEndedKind,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FlowField] = FlowKey(flowName),
            [OutcomeField] = outcome,
            [TypeField] = typeName,
            [TitleField] = title,
        });

    /// <summary>
    /// A pause of a flow run, as attention: its <c>flow</c> field carries the flow's name as <see cref="FlowKey"/> folds
    /// it, its <c>step</c> field the step the flow run was on when the pause was recorded, and its <c>type</c> and
    /// <c>title</c> fields the type and the title of the flow run's parcel. The pause's reason and lever are free text
    /// and are not fields.
    /// </summary>
    public static TriggerEvent Attention(string flowName, string step, string typeName, string title) => new(
        AttentionKind,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [FlowField] = FlowKey(flowName),
            [StepField] = step,
            [TypeField] = typeName,
            [TitleField] = title,
        });

    /// <summary>
    /// A flow's name folded to one case, so that the exact equality a trigger compares with finds two
    /// spellings of the same flow equal, as every other comparison of flow names does.
    /// </summary>
    public static string FlowKey(string flowName) => flowName.ToUpperInvariant();
}
