namespace Coanda.Core.Agents;

/// <summary>
/// The vendor, model, and effort an agent runs with, held as one value. Each part is a plain
/// string checked only for presence; the vendor adapter interprets them.
/// </summary>
public sealed record ModelSpec(string Vendor, string Model, string Effort)
{
    public override string ToString() => $"{Vendor}/{Model}/{Effort}";
}
