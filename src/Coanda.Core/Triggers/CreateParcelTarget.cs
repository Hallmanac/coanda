namespace Coanda.Core.Triggers;

/// <summary>
/// A target that creates a parcel of <see cref="ParcelType"/>, titled by the value of the event field
/// <see cref="TitleField"/>, and starts <see cref="Flow"/> on it. The names are not resolved when the
/// trigger is drafted; the trigger loop refuses a missing one when it fires.
/// </summary>
public sealed record CreateParcelTarget
{
    public string? ParcelType { get; init; }
    public string? TitleField { get; init; }
    public string? Flow { get; init; }
}
