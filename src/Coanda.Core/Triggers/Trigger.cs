using Coanda.Core.Flows;
using Coanda.Core.Types;

namespace Coanda.Core.Triggers;

/// <summary>
/// An event kind plus equality filters, and nothing more: it matches an event of its kind when
/// every filter names a field the event carries and that field's value equals the filter's value
/// exactly. A trigger never computes; a need beyond equality is a judgment step.
/// </summary>
public sealed record Trigger(string EventKind, IReadOnlyDictionary<string, string> Filters)
{
    /// <summary>The trigger a flow binding is: a parcel of the type entering the bound state.</summary>
    public static Trigger ForBinding(ParcelType type, FlowBinding binding) => new(
        TriggerEvent.ParcelEnteredKind,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TriggerEvent.TypeField] = type.Name,
            [TriggerEvent.StateField] = binding.State,
        });

    /// <summary>
    /// The trigger a follow is: a flight ending with the followed outcome, from the followed flow
    /// when the follow names one. Like every filter, each compares for exact equality.
    /// </summary>
    public static Trigger ForFollow(FlowFollow follow)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal) { [TriggerEvent.OutcomeField] = follow.Outcome };
        if (follow.Flow is not null)
        {
            filters[TriggerEvent.FlowField] = follow.Flow;
        }

        return new Trigger(TriggerEvent.FlightEndedKind, filters);
    }

    public bool Matches(TriggerEvent candidate) =>
        string.Equals(candidate.Kind, EventKind, StringComparison.Ordinal)
        && Filters.All(filter =>
            candidate.Fields.TryGetValue(filter.Key, out string? value)
            && string.Equals(value, filter.Value, StringComparison.Ordinal));
}
