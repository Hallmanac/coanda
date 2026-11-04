using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

/// <summary>A gate for tests: passes a parcel whose title does not contain <see cref="Refuses"/>, and records every check.</summary>
public sealed class StandInGate(string name = StandInGate.DefaultName, string refuses = "unready") : IGate
{
    public const string DefaultName = "stand-in";
    public const string Reason = "the title says it is not ready";

    public string Name => name;

    public string Refuses => refuses;

    public List<(string From, string To)> Checks { get; } = [];

    public GateVerdict Check(Parcel parcel, ParcelType type, string fromState, string toState)
    {
        Checks.Add((fromState, toState));
        return parcel.Title.Contains(refuses, StringComparison.Ordinal)
            ? GateVerdict.Fail(Reason)
            : GateVerdict.Pass;
    }
}
