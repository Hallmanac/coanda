using Coanda.Core.Flows;
using Coanda.Core.Types;

namespace Coanda.Core.Triggers;

/// <summary>
/// An event kind plus equality filters, and nothing more: it matches an event of its kind when
/// every filter names a field the event carries and that field's value equals the filter's value
/// exactly. A trigger never computes; a need beyond equality is an agent step.
/// </summary>
public sealed record Trigger(string EventKind, IReadOnlyDictionary<string, string> Filters)
{
    /// <summary>The trigger a flow binding is: a parcel of the type entering the bound stage.</summary>
    public static Trigger ForBinding(ParcelType type, FlowBinding binding) => new(
        TriggerEvent.ParcelEnteredKind,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TriggerEvent.TypeField] = type.Name,
            [TriggerEvent.StageField] = binding.Stage,
        });

    /// <summary>
    /// The trigger a follow is: a flow run ending with the followed outcome, from the followed flow
    /// when the follow names one. Like every filter, each compares for exact equality; the flow's
    /// name is folded as <see cref="TriggerEvent.FlowRunEnded"/> folds it, so any spelling of the
    /// followed flow's name matches it.
    /// </summary>
    public static Trigger ForFollow(FlowFollow follow)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal) { [TriggerEvent.OutcomeField] = follow.Outcome };
        if (follow.Flow is not null)
        {
            filters[TriggerEvent.FlowField] = TriggerEvent.FlowKey(follow.Flow);
        }

        return new Trigger(TriggerEvent.FlowRunEndedKind, filters);
    }

    /// <summary>
    /// The trigger a trigger definition matches by: its event kind and its filters, with a <c>flow</c>
    /// filter on a flow run's end folded as <see cref="ForFollow"/> folds a follow's flow name, since the
    /// definition stores it as written. Every other filter compares exactly, as written.
    /// </summary>
    public static Trigger ForDefinition(TriggerDefinition definition)
    {
        var filters = new Dictionary<string, string>(definition.Filters, StringComparer.Ordinal);
        if (string.Equals(definition.EventKind, TriggerEvent.FlowRunEndedKind, StringComparison.Ordinal)
            && filters.TryGetValue(TriggerEvent.FlowField, out string? flow))
        {
            filters[TriggerEvent.FlowField] = TriggerEvent.FlowKey(flow);
        }

        return new Trigger(definition.EventKind, filters);
    }

    public bool Matches(TriggerEvent candidate) =>
        string.Equals(candidate.Kind, EventKind, StringComparison.Ordinal)
        && Filters.All(filter =>
            candidate.Fields.TryGetValue(filter.Key, out string? value)
            && string.Equals(value, filter.Value, StringComparison.Ordinal));
}
