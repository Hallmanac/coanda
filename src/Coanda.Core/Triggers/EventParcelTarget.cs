namespace Coanda.Core.Triggers;

/// <summary>A target that starts <see cref="Flow"/> on the parcel the event names.</summary>
public sealed record EventParcelTarget
{
    public string? Flow { get; init; }
}
