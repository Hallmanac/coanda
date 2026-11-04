using Coanda.Core.Types;

namespace Coanda.Core.Parcels;

/// <summary>
/// Passes a move only when the parcel holds a value in every field its type declares as required to
/// leave the move's from state, and otherwise names every such field that holds none. It reads the
/// type's field declarations and names no field of its own, so it serves any type that declares fields.
/// </summary>
public sealed class RequiredFieldsGate : IGate
{
    public const string GateName = "required-fields";

    public string Name => GateName;

    public GateVerdict Check(Parcel parcel, ParcelType type, string fromState, string toState)
    {
        List<string> empty = [.. type.Fields
            .Where(f => f.IsRequiredToLeave(fromState) && parcel.ValueOf(f.Name) is null)
            .Select(f => $"'{f.Name}'")];

        return empty.Count == 0
            ? GateVerdict.Pass
            : GateVerdict.Fail(
                $"a '{type.Name}' leaves '{fromState}' only with a value in every field required to leave it, and {string.Join(", ", empty)} {(empty.Count == 1 ? "has" : "have")} none.");
    }
}
