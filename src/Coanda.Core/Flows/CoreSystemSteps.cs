namespace Coanda.Core.Flows;

/// <summary>
/// The names of the domain-free system steps only the daemon performs. A catalog is names only: what a
/// system step does lives in the daemon, and a flow's validation takes the known names as an input.
/// Anything specific to a domain is a separate catalog that the caller passes in beside this one.
/// </summary>
public static class CoreSystemSteps
{
    public const string WaitForHuman = "wait-for-human";
    public const string WaitForExternalEvent = "wait-for-external-event";
    public const string Timer = "timer";
    public const string EmitOutcome = "emit-outcome";
    public const string MoveParcel = "move-parcel";
    public const string DeriveParcels = "derive-parcels";

    public static readonly IReadOnlyList<string> All =
    [
        WaitForHuman, WaitForExternalEvent, Timer, EmitOutcome, MoveParcel, DeriveParcels,
    ];
}
