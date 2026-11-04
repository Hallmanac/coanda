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

    /// <summary>
    /// Waits for the flight a flow binding starts on the parcel, failing if none appears or more than one does.
    /// A follower of that flight is not a second bound flight: a fast flight can end and start its follower
    /// between two reads, so only the flights with no parent count.
    /// </summary>
    public static async Task<FlightView> BoundAsync(DaemonClient client, string project, Guid parcelId)
    {
        using var patience = new CancellationTokenSource(Patience);
        List<FlightView> bound = await BoundOfParcelAsync(client, project, parcelId);
        while (bound.Count == 0)
        {
            if (patience.IsCancellationRequested)
            {
                throw new TimeoutException($"No flight started on parcel {parcelId}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            bound = await BoundOfParcelAsync(client, project, parcelId);
        }

        return bound.Should().ContainSingle("a binding starts one flight per entry").Subject;
    }

    private static async Task<List<FlightView>> BoundOfParcelAsync(DaemonClient client, string project, Guid parcelId) =>
        [.. (await OfParcelAsync(client, project, parcelId)).Where(f => f.ParentFlightId is null)];

    public static Task<List<FlightView>> OfParcelAsync(DaemonClient client, string project, Guid parcelId) =>
        client.GetAsync<List<FlightView>>($"/flights?project={project}&parcel={parcelId}", CancellationToken.None);

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
                    $"Flight {flightId} did not reach '{status}'; it is {flight.Status} at '{flight.CurrentStep}' ({flight.PauseReason}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            flight = await ShowAsync(client, project, flightId);
        }

        return flight;
    }

    /// <summary>Waits until <paramref name="condition"/> holds, failing after the same patience.</summary>
    public static Task UntilAsync(Func<bool> condition, string what) =>
        UntilAsync(() => Task.FromResult(condition()), what);

    /// <summary>Waits until the asynchronous <paramref name="condition"/> holds, failing after the same patience.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        using var patience = new CancellationTokenSource(Patience);
        while (!await condition())
        {
            if (patience.IsCancellationRequested)
            {
                throw new TimeoutException($"Gave up waiting until {what}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
        }
    }
}
