using Coanda.Core.Types;

namespace Coanda.Core.Parcels;

/// <summary>
/// A named check a parcel type binds to a move: given the parcel, its type, and the move, it passes
/// or gives the reason it does not. A gate is pure and reads only the parcel and its type; it names
/// nothing about any one lifecycle. The move it is given is the one the binding names, so a move out
/// of the paused state is judged as the bound move into the same target.
/// </summary>
public interface IGate
{
    string Name { get; }

    GateVerdict Check(Parcel parcel, ParcelType type, string fromState, string toState);
}
