namespace Coanda.Core.Parcels;

/// <summary>What a gate says about a move: it passes, or it fails with a reason.</summary>
public abstract record GateVerdict
{
    private GateVerdict()
    {
    }

    public static GateVerdict Pass { get; } = new Passed();

    public static GateVerdict Fail(string reason) => new Failed(reason);

    public sealed record Passed : GateVerdict;

    public sealed record Failed(string Reason) : GateVerdict;
}
