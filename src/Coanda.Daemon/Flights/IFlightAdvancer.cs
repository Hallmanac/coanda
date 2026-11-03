namespace Coanda.Daemon.Flights;

/// <summary>
/// The daemon's one request to advance a flight by its id. The start endpoint, a resume, and the
/// daemon's own start all make it; <see cref="FlightRunner"/> answers it.
/// </summary>
public interface IFlightAdvancer
{
    /// <summary>
    /// Asks for the flight to be advanced until it ends, parks, or stops. A request for a flight
    /// already being advanced joins that work rather than starting a second worker. The task
    /// completes when no worker is advancing the flight any more.
    /// </summary>
    Task RequestAdvance(string project, Guid flightId);
}
