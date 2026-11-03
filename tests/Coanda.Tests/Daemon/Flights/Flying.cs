using Coanda.Cli;
using Coanda.Daemon.Api;

namespace Coanda.Tests.Daemon.Flights;

/// <summary>Drives flights through the running daemon's endpoints, the way <c>coa</c> does.</summary>
public static class Flying
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public static async Task<ParcelView> TaskAtBuildAsync(DaemonClient client, string project, string title)
    {
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(project, "task", title, null), CancellationToken.None);
        return await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(project, parcel.Id, "build"), CancellationToken.None);
    }

    public static Task<FlightView> StartAsync(DaemonClient client, string project, Guid parcelId, string flow) =>
        client.PostAsync<FlightView>("/flights", new FlightStartRequest(project, parcelId, flow), CancellationToken.None);

    public static Task<FlightView> ShowAsync(DaemonClient client, string project, Guid flightId) =>
        client.GetAsync<FlightView>($"/flights/{flightId}?project={project}", CancellationToken.None);

    /// <summary>Reads the flight until it reaches the status, failing with what it last showed.</summary>
    public static async Task<FlightView> UntilAsync(DaemonClient client, string project, Guid flightId, string status)
    {
        using var patience = new CancellationTokenSource(Patience);
        FlightView flight = await ShowAsync(client, project, flightId);
        while (flight.Status != status)
        {
            if (patience.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Flight {flightId} did not reach '{status}'; it is {flight.Status} at '{flight.CurrentStep}' ({flight.ParkReason}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            flight = await ShowAsync(client, project, flightId);
        }

        return flight;
    }

    /// <summary>Waits until <paramref name="condition"/> holds, failing after the same patience.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        using var patience = new CancellationTokenSource(Patience);
        while (!condition())
        {
            if (patience.IsCancellationRequested)
            {
                throw new TimeoutException($"Gave up waiting until {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        }
    }
}
