using Coanda.Cli;
using Coanda.Daemon.Api.Endpoints;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>Drives flows through the running daemon's endpoints, the way <c>coa</c> does.</summary>
public static class Flowing
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Adds a task, sets the objective and criteria its move out of refine requires and, unless
    /// <paramref name="pullRequest"/> is false, the pull request its move out of build requires, and
    /// moves it to build.
    /// </summary>
    public static async Task<ParcelView> TaskAtBuildAsync(DaemonClient client, string project, string title, bool pullRequest = true)
    {
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(project, "task", title, null), CancellationToken.None);
        await client.BriefAsync(project, parcel.Id, pullRequest, CancellationToken.None);
        return await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(project, parcel.Id, "build"), CancellationToken.None);
    }

    /// <summary>
    /// Waits for the flow a contour binding starts on the parcel, failing if none appears or more than one does.
    /// A follower of that flow is not a second bound flow: a fast flow can end and start its follower
    /// between two reads, so only the flows with no parent count.
    /// </summary>
    public static async Task<FlowView> BoundAsync(DaemonClient client, string project, Guid parcelId)
    {
        using var patience = new CancellationTokenSource(Patience);
        List<FlowView> bound = await BoundOfParcelAsync(client, project, parcelId);
        while (bound.Count == 0)
        {
            if (patience.IsCancellationRequested)
            {
                throw new TimeoutException($"No flow started on parcel {parcelId}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            bound = await BoundOfParcelAsync(client, project, parcelId);
        }

        return bound.Should().ContainSingle("a binding starts one flow per entry").Subject;
    }

    private static async Task<List<FlowView>> BoundOfParcelAsync(DaemonClient client, string project, Guid parcelId) =>
        [.. (await OfParcelAsync(client, project, parcelId)).Where(f => f.ParentFlowId is null)];

    public static Task<List<FlowView>> OfParcelAsync(DaemonClient client, string project, Guid parcelId) =>
        client.GetAsync<List<FlowView>>($"/flows?project={project}&parcel={parcelId}", CancellationToken.None);

    public static Task<FlowView> StartAsync(DaemonClient client, string project, Guid parcelId, string contour) =>
        client.PostAsync<FlowView>("/flows", new FlowStartRequest(project, parcelId, contour), CancellationToken.None);

    public static Task<FlowView> PauseAsync(DaemonClient client, string project, Guid flowId, string reason) =>
        client.PostAsync<FlowView>($"/flows/{flowId}/pause", new FlowPauseRequest(project, reason), CancellationToken.None);

    public static Task<FlowView> ResumeAsync(DaemonClient client, string project, Guid flowId, string? note) =>
        client.PostAsync<FlowView>($"/flows/{flowId}/resume", new FlowResumeRequest(project, note), CancellationToken.None);

    public static Task<FlowView> StopAsync(DaemonClient client, string project, Guid flowId, string reason) =>
        client.PostAsync<FlowView>($"/flows/{flowId}/stop", new FlowStopRequest(project, reason), CancellationToken.None);

    public static Task<FlowView> ShowAsync(DaemonClient client, string project, Guid flowId) =>
        client.GetAsync<FlowView>($"/flows/{flowId}?project={project}", CancellationToken.None);

    /// <summary>Reads the flow until it reaches the status, failing with what it last showed.</summary>
    public static async Task<FlowView> UntilAsync(DaemonClient client, string project, Guid flowId, string status)
    {
        using var patience = new CancellationTokenSource(Patience);
        FlowView flow = await ShowAsync(client, project, flowId);
        while (flow.Status != status)
        {
            if (patience.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Flow {flowId} did not reach '{status}'; it is {flow.Status} at '{flow.CurrentStep}' ({flow.PauseReason}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            flow = await ShowAsync(client, project, flowId);
        }

        return flow;
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
