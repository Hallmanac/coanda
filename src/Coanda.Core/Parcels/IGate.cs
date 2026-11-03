namespace Coanda.Core.Parcels;

/// <summary>
/// A named check a parcel type binds to a move: given the parcel and the move, it passes or gives
/// the reason it does not. A gate is pure and reads only the parcel; it names nothing about any
/// one lifecycle.
/// </summary>
public interface IGate
{
    string Name { get; }

    GateVerdict Check(Parcel parcel, string fromState, string toState);
}
