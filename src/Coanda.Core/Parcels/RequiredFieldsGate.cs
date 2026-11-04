using Coanda.Core.Types;

namespace Coanda.Core.Parcels;

/// <summary>
/// Passes a move only when the parcel holds a value in every field its type declares as required to
/// leave the move's from stage, and otherwise names every such field that holds none. On a resume,
/// where the parcel itself still stands in the paused stage and the move is the bound one into the
/// resume's target, it also requires every field the type requires to enter that target
/// (<see cref="ParcelType.RequiredToEnter"/>), since a resume skips the moves that would have checked
/// them. The resume check is additive: it keeps the leave check of the bound move's from stage, so a
/// resume, which runs every binding into the target, still asks for what each bound move into the
/// target asks for. Dropping it would let a parcel resume into a stage with two bound entry
/// predecessors while holding neither predecessor's leave fields, since the intersection over routes
/// is empty there. It reads the type's field declarations and names no field of its own, so it serves
/// any type that declares fields.
/// </summary>
public sealed class RequiredFieldsGate : IGate
{
    public const string GateName = "required-fields";

    public string Name => GateName;

    public GateVerdict Check(Parcel parcel, ParcelType type, string fromStage, string toStage)
    {
        bool resuming = type.IsPaused(parcel.Stage) && !type.IsTerminal(toStage) && !type.IsPaused(toStage);
        IReadOnlyList<string> toEnter = resuming
            ? type.RequiredToEnter(toStage)
            : [];

        List<string> empty = [.. type.Fields
            .Where(f => (f.IsRequiredToLeave(fromStage) || toEnter.Contains(f.Name, StringComparer.Ordinal)) && parcel.ValueOf(f.Name) is null)
            .Select(f => $"'{f.Name}'")];

        string have = empty.Count == 1
            ? "has"
            : "have";
        return empty.Count == 0
            ? GateVerdict.Pass
            : GateVerdict.Fail(resuming
                ? $"a '{type.Name}' resumes into '{toStage}' only with a value in every field required to enter it, which every route into it would have required, and in every field required to leave '{fromStage}', and {string.Join(", ", empty)} {have} none."
                : $"a '{type.Name}' leaves '{fromStage}' only with a value in every field required to leave it, and {string.Join(", ", empty)} {have} none.");
    }
}
