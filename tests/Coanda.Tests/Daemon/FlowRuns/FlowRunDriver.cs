using Coanda.Cli;
using Coanda.Daemon.Api.Endpoints;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>Drives flow runs through the running daemon's endpoints, the way <c>coa</c> does.</summary>
public static class FlowRunDriver
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Adds a task, sets the objective and criteria its move out of refine requires and, when
    /// <paramref name="pullRequest"/> is true, the pull request its move out of build requires, and
    /// moves it to build. The shipped build contour's open-pr step records the pull request itself,
    /// so only a test that moves the task out of build some other way asks for it.
    /// </summary>
    public static async Task<ParcelView> TaskAtBuildAsync(DaemonClient client, string project, string title, bool pullRequest = false)
    {
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(project, "task", title, null), CancellationToken.None);
        await client.BriefAsync(project, parcel.Id, pullRequest, CancellationToken.None);
        return await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(project, parcel.Id, "build"), CancellationToken.None);
    }

    /// <summary>
    /// Waits for the flow run a contour binding starts on the parcel, failing if none appears or more than one does.
    /// A follower of that flow run is not a second bound flow run: a fast flow run can end and start its follower
    /// between two reads, so only the flow runs with no parent count.
    /// </summary>
    public static async Task<FlowRunView> BoundAsync(DaemonClient client, string project, Guid parcelId)
    {
        using var patience = new CancellationTokenSource(Patience);
        List<FlowRunView> bound = await BoundOfParcelAsync(client, project, parcelId);
        while (bound.Count == 0)
        {
            if (patience.IsCancellationRequested)
            {
                throw new TimeoutException($"No flow run started on parcel {parcelId}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            bound = await BoundOfParcelAsync(client, project, parcelId);
        }

        return bound.Should().ContainSingle("a binding starts one flow run per entry").Subject;
    }

    private static async Task<List<FlowRunView>> BoundOfParcelAsync(DaemonClient client, string project, Guid parcelId) =>
        [.. (await OfParcelAsync(client, project, parcelId)).Where(f => f.ParentFlowRunId is null)];

    public static Task<List<FlowRunView>> OfParcelAsync(DaemonClient client, string project, Guid parcelId) =>
        client.GetAsync<List<FlowRunView>>($"/flow-runs?project={project}&parcel={parcelId}", CancellationToken.None);

    public static Task<FlowRunView> StartAsync(DaemonClient client, string project, Guid parcelId, string contour) =>
        client.PostAsync<FlowRunView>("/flow-runs", new FlowRunStartRequest(project, parcelId, contour), CancellationToken.None);

    public static Task<FlowRunView> PauseAsync(DaemonClient client, string project, Guid flowRunId, string reason) =>
        client.PostAsync<FlowRunView>($"/flow-runs/{flowRunId}/pause", new FlowRunPauseRequest(project, reason), CancellationToken.None);

    public static Task<FlowRunView> ResumeAsync(DaemonClient client, string project, Guid flowRunId, string? note) =>
        client.PostAsync<FlowRunView>($"/flow-runs/{flowRunId}/resume", new FlowRunResumeRequest(project, note), CancellationToken.None);

    public static Task<FlowRunView> StopAsync(DaemonClient client, string project, Guid flowRunId, string reason) =>
        client.PostAsync<FlowRunView>($"/flow-runs/{flowRunId}/stop", new FlowRunStopRequest(project, reason), CancellationToken.None);

    public static Task<FlowRunView> ShowAsync(DaemonClient client, string project, Guid flowRunId) =>
        client.GetAsync<FlowRunView>($"/flow-runs/{flowRunId}?project={project}", CancellationToken.None);

    /// <summary>Reads the flow run until it reaches the status, failing with what it last showed.</summary>
    public static async Task<FlowRunView> UntilAsync(DaemonClient client, string project, Guid flowRunId, string status)
    {
        using var patience = new CancellationTokenSource(Patience);
        FlowRunView flowRun = await ShowAsync(client, project, flowRunId);
        while (flowRun.Status != status)
        {
            if (patience.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Flow run {flowRunId} did not reach '{status}'; it is {flowRun.Status} at '{flowRun.CurrentStep}' ({flowRun.PauseReason}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), CancellationToken.None);
            flowRun = await ShowAsync(client, project, flowRunId);
        }

        return flowRun;
    }

    /// <summary>Waits for the flow run of <paramref name="contour"/> that the end of <paramref name="parentId"/> starts on the parcel.</summary>
    public static async Task<FlowRunView> FollowerAsync(DaemonClient client, string project, Guid parcelId, Guid parentId, string contour)
    {
        FlowRunView? follower = null;
        await UntilAsync(
            async () =>
            {
                List<FlowRunView> flowRuns = await OfParcelAsync(client, project, parcelId);
                follower = flowRuns.SingleOrDefault(f => f.ParentFlowRunId == parentId && f.ContourName == contour);
                return follower is not null;
            },
            $"a flow run of '{contour}' follows flow run {parentId}");
        return follower ?? throw new InvalidOperationException("The wait ended with no follower.");
    }

    /// <summary>
    /// Waits for the shipped merge contour's flow run that follows the build flow run <paramref name="buildId"/>
    /// to end, which merges the pull request and closes the task, and returns it.
    /// </summary>
    public static async Task<FlowRunView> MergedAsync(DaemonClient client, string project, Guid parcelId, Guid buildId)
    {
        FlowRunView merge = await FollowerAsync(client, project, parcelId, buildId, "merge");
        return await UntilAsync(client, project, merge.Id, "ended");
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
