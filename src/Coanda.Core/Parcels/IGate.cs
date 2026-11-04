using Coanda.Core.Types;

namespace Coanda.Core.Parcels;

/// <summary>
/// A named check a parcel type binds to a move: given the parcel, its type, and the move, it passes
/// or gives the reason it does not. A gate is pure and reads only the parcel and its type; it names
/// nothing about any one lifecycle. The move it is given is the one the binding names, so a move out
/// of the paused stage is judged as the bound move into the same target. The parcel is as it stands
/// before the move, so its own stage is where the move actually starts, which is how a gate tells a
/// resume from the bound move.
/// </summary>
public interface IGate
{
    string Name { get; }

    GateVerdict Check(Parcel parcel, ParcelType type, string fromStage, string toStage);
}
