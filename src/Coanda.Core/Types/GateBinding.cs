namespace Coanda.Core.Types;

/// <summary>A gate that must pass before a parcel of the type moves from <see cref="From"/> to <see cref="To"/>.</summary>
public sealed record GateBinding(string From, string To, string Gate);
