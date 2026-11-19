using Coanda.Cli;
using Coanda.Core.Flows;
using Coanda.Core.FlowRuns.Events;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Store;
using Coanda.Daemon.Triggers;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// A follower of a flow run's end whose start straight from that end fails for a reason other than a refusal is
/// passed over, and the later followers still start; a stop while such a start is held open leaves the end to the
/// next start, which starts each follower once.
/// </summary>
public sealed class FollowFailureTests
{
    private const string Project = "alpha";
    private const string Merge = "merge";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A follower of the build flow's outcome that does not close, so it is decided ahead of the shipped merge.</summary>
    private static readonly Flow AlsoAfterBuild = new()
    {
        Name = "also-after-build",
        Accepts = ["task"],
        Outcome = "followed",
        Budget = 1m,
        SpawnCap = 1,
        Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 },
        Steps =
        [
            new FlowStep
            {
                Name = "look",
                SystemStep = SoftwareSteps.Worktree,
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    [Fact]
    public async Task A_follower_whose_start_from_the_end_fails_is_passed_over_and_the_closing_follower_still_starts_and_closes_the_parcel()
    {
        using var temp = new TemporaryHome();
        int failed = 0;
        Task FailAlso(DirectFollowStart start, CancellationToken token)
        {
            if (start.Flow == AlsoAfterBuild.Name)
            {
                Interlocked.Increment(ref failed);
                throw new InvalidOperationException("A start that fails for a reason other than a refusal.");
            }

            return Task.CompletedTask;
        }

        var decisions = new List<FollowChoice>();
        Task Record(FollowChoice decision, CancellationToken token)
        {
            lock (decisions)
            {
                decisions.Add(decision);
            }

            return Task.CompletedTask;
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), DirectFollowStarting = FailAlso, FollowDecided = Record }, None);
        using var client = new DaemonClient(temp.Home);
        (Guid parcelId, FlowRunView build) = await BuildAsync(daemon, client);

        FlowRunView merge = await FlowRunDriver.MergedAsync(client, Project, parcelId, build.Id);

        Assert.Equal("merged", merge.Outcome);
        Assert.Equal("closed", (await client.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", None)).Stage);
        List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(client, Project, parcelId);
        Assert.Equal(["build", Merge], flowRuns.OrderBy(f => f.StartedAt).Select(f => f.FlowName));
        Assert.Equal(1, failed);
        lock (decisions)
        {
            Assert.Equal(
                [(AlsoAfterBuild.Name, false), (Merge, false)],
                decisions.Where(d => d.ParentFlowRunId == build.Id).Select(d => (d.Flow, d.Waits)));
        }

        Assert.Null(await QueueAsync(daemon.Services.GetRequiredService<IDocumentStore>(), parcelId));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_stop_while_a_follower_starts_from_the_end_leaves_the_end_to_the_next_start_which_starts_each_follower_once()
    {
        using var temp = new TemporaryHome();
        Guid parcelId;
        Guid buildId;
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task HoldAlso(DirectFollowStart start, CancellationToken token)
        {
            if (start.Flow == AlsoAfterBuild.Name)
            {
                holding.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }

        await using (RunningDaemon first = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), DirectFollowStarting = HoldAlso }, None))
        {
            using var client = new DaemonClient(temp.Home);
            (parcelId, FlowRunView build) = await BuildAsync(first, client);
            buildId = build.Id;
            await holding.Task.WaitAsync(TestPatience.Bound);
            await first.StopAsync(None);
        }

        await using (IDocumentStore stopped = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            long ended = await EndedSequenceAsync(stopped, buildId);
            Assert.True(await PositionAsync(stopped) < ended, "The loop's position passed the end whose handling the stop interrupted.");
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var again = new DaemonClient(temp.Home);
        await FlowRunDriver.MergedAsync(again, Project, parcelId, buildId);

        List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(again, Project, parcelId);
        Assert.Equal(["build", AlsoAfterBuild.Name, Merge], flowRuns.OrderBy(f => f.StartedAt).Select(f => f.FlowName));
        Assert.All(flowRuns.Where(f => f.FlowName != "build"), f => Assert.Equal(buildId, f.ParentFlowRunId));
        Assert.Equal("closed", (await again.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", None)).Stage);

        await second.StopAsync(None);
    }

    private static async Task<(Guid ParcelId, FlowRunView Build)> BuildAsync(RunningDaemon daemon, DaemonClient client)
    {
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await Publishing.FlowAsync(daemon, Project, AlsoAfterBuild);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Build, then follow straight from the end");
        FlowRunView build = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        return (parcel.Id, await FlowRunDriver.UntilAsync(client, Project, build.Id, "ended"));
    }

    private static async Task<DeferredFollows?> QueueAsync(IDocumentStore store, Guid parcelId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return await session.LoadAsync<DeferredFollows>(parcelId, None);
    }

    private static async Task<long> PositionAsync(IDocumentStore store)
    {
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        TriggerPosition? position = await session.LoadAsync<TriggerPosition>(TriggerPosition.LoopId, None);
        return position?.Sequence ?? 0;
    }

    private static async Task<long> EndedSequenceAsync(IDocumentStore store, Guid flowRunId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(flowRunId, token: None);
        return events.Single(e => e.Data is FlowRunEnded).Sequence;
    }
}
