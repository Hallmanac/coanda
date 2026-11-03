namespace Coanda.Core.Flights;

/// <summary>
/// Where a flight stands in its life. A running or parked flight is live; an ended or stopped one
/// is not, and its parcel is free for another.
/// </summary>
public enum FlightStatus
{
    Running,
    Parked,
    Ended,
    Stopped,
}
