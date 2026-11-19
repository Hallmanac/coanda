using Coanda.Cli;
using Coanda.Core.Flows;
using Coanda.Core.Parcels.Events;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Store;
using Coanda.Daemon.Triggers;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// A parcel's follow queue: a follow whose start fails for a reason other than a refusal is passed over, a stop
/// leaves it waiting, and a queue whose parcel is free when the daemon starts proceeds then.
/// </summary>
public sealed class FollowQueueTests
{
    private const string Project = "alpha";
    private const string Merge = "merge";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The first follower of the build flow's outcome, whose one step is <see cref="RunningSystemStep"/>, which a test holds open.</summary>
    private static readonly Flow HeldAfterBuild = RunningSystemStep.FlowNamed("after-build") with
    {
        Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 },
    };

    /// <summary>A second follower of the same end, which waits in the parcel's follow queue behind the held one, ahead of the shipped merge.</summary>
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
    public async Task A_follow_whose_start_fails_leaves_the_queue_and_the_closing_follower_behind_it_still_starts_and_closes_the_parcel()
    {
        using var temp = new TemporaryHome();
        int failed = 0;
        Task FailAlso(DeferredFollowStart start, CancellationToken token)
        {
            if (start.Flow == AlsoAfterBuild.Name)
            {
                Interlocked.Increment(ref failed);
                throw new InvalidOperationException("A start that fails for a reason other than a refusal.");
            }

            return Task.CompletedTask;
        }

        await using Chain chain = await Chain.StartAsync(temp, FailAlso, holdForever: false, None);
        FlowRunView merge = await FlowRunDriver.MergedAsync(chain.Client, Project, chain.ParcelId, chain.Build.Id);

        Assert.Equal("merged", merge.Outcome);
        Assert.Equal("closed", (await chain.Client.GetAsync<ParcelView>($"/parcels/{chain.ParcelId}?project={Project}", None)).Stage);
        List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(chain.Client, Project, chain.ParcelId);
        Assert.Equal(["build", HeldAfterBuild.Name, Merge], flowRuns.OrderBy(f => f.StartedAt).Select(f => f.FlowName));
        Assert.Equal(1, failed);
        Assert.Null(await QueueAsync(chain.Daemon.Services.GetRequiredService<IDocumentStore>(), chain.ParcelId));

        await chain.Daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_failed_start_of_the_last_follow_in_the_queue_leaves_the_parcel_with_no_queue()
    {
        using var temp = new TemporaryHome();
        var mergeFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task FailMerge(DeferredFollowStart start, CancellationToken token)
        {
            if (start.Flow == Merge)
            {
                mergeFailed.TrySetResult();
                throw new InvalidOperationException("A start that fails for a reason other than a refusal.");
            }

            return Task.CompletedTask;
        }

        await using Chain chain = await Chain.StartAsync(temp, FailMerge, holdForever: false, None);
        FlowRunView also = await FlowRunDriver.FollowerAsync(chain.Client, Project, chain.ParcelId, chain.Build.Id, AlsoAfterBuild.Name);
        await FlowRunDriver.UntilAsync(chain.Client, Project, also.Id, "ended");
        await mergeFailed.Task.WaitAsync(TestPatience.Bound);

        // With no queue left, a closing follower of a later end on the parcel is not held behind one.
        IDocumentStore store = chain.Daemon.Services.GetRequiredService<IDocumentStore>();
        await FlowRunDriver.UntilAsync(async () => await QueueAsync(store, chain.ParcelId) is null, $"parcel {chain.ParcelId} has no follow queue");
        List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(chain.Client, Project, chain.ParcelId);
        Assert.Equal(["build", HeldAfterBuild.Name, AlsoAfterBuild.Name], flowRuns.OrderBy(f => f.StartedAt).Select(f => f.FlowName));
        Assert.NotEqual("closed", (await chain.Client.GetAsync<ParcelView>($"/parcels/{chain.ParcelId}?project={Project}", None)).Stage);

        await chain.Daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_stop_while_a_deferred_follow_starts_leaves_it_queued_before_the_release_and_the_next_start_starts_each_follower_once()
    {
        using var temp = new TemporaryHome();
        (Guid parcelId, Guid buildId, long heldReleased) = await StrandAsync(temp);

        await using (IDocumentStore stopped = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            DeferredFollows? queue = await QueueAsync(stopped, parcelId);
            Assert.NotNull(queue);
            Assert.Equal([AlsoAfterBuild.Name, Merge], queue.Pending.Select(p => p.Flow));
            Assert.True(await PositionAsync(stopped) < heldReleased, "The loop's position passed the release whose handling the stop interrupted.");
        }

        // The next start handles the release again before it resumes the queues, and each follower starts once.
        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var client = new DaemonClient(temp.Home);
        await FlowRunDriver.MergedAsync(client, Project, parcelId, buildId);
        await AssertEachFollowerOnceAsync(client, parcelId, buildId);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_queue_on_a_free_parcel_proceeds_when_the_daemon_starts_and_passes_over_a_follow_already_started()
    {
        using var temp = new TemporaryHome();
        (Guid parcelId, Guid buildId, long _) = await StrandAsync(temp);

        // The loop has handled every entry, so no release is left to start the queue, and the queue still
        // names the follower of the build that already started and ended.
        await using (IDocumentStore stopped = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            long head = await stopped.Database.FetchHighestEventSequenceNumber(None);
            await using IDocumentSession node = stopped.LightweightSession(Tenants.Node);
            node.Store(new TriggerPosition { Sequence = head });
            await node.SaveChangesAsync(None);

            await using IDocumentSession session = stopped.LightweightSession(Project);
            session.Store(new DeferredFollows
            {
                Id = parcelId,
                Pending =
                [
                    new PendingFollow(buildId, HeldAfterBuild.Name),
                    new PendingFollow(buildId, AlsoAfterBuild.Name),
                    new PendingFollow(buildId, Merge, Closes: true),
                ],
            });
            await session.SaveChangesAsync(None);
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var client = new DaemonClient(temp.Home);
        await FlowRunDriver.MergedAsync(client, Project, parcelId, buildId);
        await AssertEachFollowerOnceAsync(client, parcelId, buildId);
        Assert.Equal("closed", (await client.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", None)).Stage);

        await second.StopAsync(None);
    }

    [Fact]
    public async Task A_queue_whose_parcel_has_a_live_flow_run_at_start_starts_nothing_and_waits_for_its_release()
    {
        using var temp = new TemporaryHome();
        Guid parcelId;
        Guid buildId;
        Guid heldId;
        await using (Chain chain = await Chain.StartAsync(temp, null, holdForever: true, None))
        {
            parcelId = chain.ParcelId;
            buildId = chain.Build.Id;
            heldId = (await FlowRunDriver.FollowerAsync(chain.Client, Project, parcelId, buildId, HeldAfterBuild.Name)).Id;
            IDocumentStore store = chain.Daemon.Services.GetRequiredService<IDocumentStore>();
            long buildReleased = await ReleaseSequenceAsync(store, parcelId, buildId);
            await FlowRunDriver.UntilAsync(async () => await PositionAsync(store) >= buildReleased, $"the trigger loop handles the release of flow run {buildId}");
            await chain.Daemon.StopAsync(None);
        }

        var held = new RunningSystemStep();
        await using RunningDaemon second = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor(), SystemSteps = [held] }, None);
        using var client = new DaemonClient(temp.Home);
        IDocumentStore running = second.Services.GetRequiredService<IDocumentStore>();

        // An entry recorded after the start: once the loop is past it, it has resumed the queues.
        ParcelView later = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Recorded after the start", null), None);
        long laterCreated = await FirstSequenceAsync(running, later.Id);
        await FlowRunDriver.UntilAsync(async () => await PositionAsync(running) >= laterCreated, "the trigger loop handles an entry recorded after its start");

        Assert.Equal("running", (await FlowRunDriver.ShowAsync(client, Project, heldId)).Status);
        Assert.DoesNotContain(await FlowRunDriver.OfParcelAsync(client, Project, parcelId), f => f.FlowName == AlsoAfterBuild.Name);
        DeferredFollows? queue = await QueueAsync(running, parcelId);
        Assert.NotNull(queue);
        Assert.Equal([AlsoAfterBuild.Name, Merge], queue.Pending.Select(p => p.Flow));

        // The held flow run's release starts the queue.
        await FlowRunDriver.StopAsync(client, Project, heldId, "release the parcel");
        await FlowRunDriver.MergedAsync(client, Project, parcelId, buildId);
        await AssertEachFollowerOnceAsync(client, parcelId, buildId);

        await second.StopAsync(None);
    }

    /// <summary>
    /// Runs the chain until the queue holds the second follower and the merge and the held follower has ended,
    /// then stops the daemon while the second follower's start from the queue is held open. Returns the parcel,
    /// the build flow run, and the sequence of the held follower's release, whose handling the stop interrupted.
    /// </summary>
    private static async Task<(Guid ParcelId, Guid BuildId, long HeldReleased)> StrandAsync(TemporaryHome temp)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task HoldAlso(DeferredFollowStart start, CancellationToken token)
        {
            if (start.Flow == AlsoAfterBuild.Name)
            {
                holding.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }

        await using Chain chain = await Chain.StartAsync(temp, HoldAlso, holdForever: false, None);
        await holding.Task.WaitAsync(TestPatience.Bound);
        FlowRunView held = await FlowRunDriver.FollowerAsync(chain.Client, Project, chain.ParcelId, chain.Build.Id, HeldAfterBuild.Name);
        long heldReleased = await ReleaseSequenceAsync(chain.Daemon.Services.GetRequiredService<IDocumentStore>(), chain.ParcelId, held.Id);
        await chain.Daemon.StopAsync(None);
        return (chain.ParcelId, chain.Build.Id, heldReleased);
    }

    private static async Task AssertEachFollowerOnceAsync(DaemonClient client, Guid parcelId, Guid buildId)
    {
        List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(client, Project, parcelId);
        Assert.Equal(["build", HeldAfterBuild.Name, AlsoAfterBuild.Name, Merge], flowRuns.OrderBy(f => f.StartedAt).Select(f => f.FlowName));
        Assert.All(flowRuns.Where(f => f.FlowName != "build"), f => Assert.Equal(buildId, f.ParentFlowRunId));
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

    private static async Task<long> ReleaseSequenceAsync(IDocumentStore store, Guid parcelId, Guid flowRunId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: None);
        return events.Single(e => e.Data is ParcelFlowRunReleased released && released.FlowRunId == flowRunId).Sequence;
    }

    private static async Task<long> FirstSequenceAsync(IDocumentStore store, Guid parcelId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: None);
        return events[0].Sequence;
    }

    /// <summary>
    /// A running daemon with a task built to its end, whose held follower runs while the second follower and the
    /// shipped merge wait in the parcel's queue. The held follower ends once the second one waits, unless
    /// <c>holdForever</c>, when it runs until the daemon stops.
    /// </summary>
    private sealed class Chain(RunningDaemon daemon, DaemonClient client, Guid parcelId, FlowRunView build) : IAsyncDisposable
    {
        public RunningDaemon Daemon { get; } = daemon;

        public DaemonClient Client { get; } = client;

        public Guid ParcelId { get; } = parcelId;

        public FlowRunView Build { get; } = build;

        public static async Task<Chain> StartAsync(
            TemporaryHome temp, Func<DeferredFollowStart, CancellationToken, Task>? starting, bool holdForever, CancellationToken cancellationToken)
        {
            var secondWaits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = new RunningSystemStep
            {
                Answer = async (_, token) =>
                {
                    await secondWaits.Task.WaitAsync(token);
                    if (holdForever)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }

                    return RunningSystemStep.Done();
                },
            };

            // The held follower ends only once the second follower waits in the queue, so the second one never
            // starts straight from the build's end, ahead of the queue.
            Task SignalSecondWaits(FollowChoice decision, CancellationToken token)
            {
                if (decision.Flow == AlsoAfterBuild.Name && decision.Waits)
                {
                    secondWaits.TrySetResult();
                }

                return Task.CompletedTask;
            }

            DaemonOptions options = new()
            {
                Executor = Scripts.CleanBuildExecutor(),
                SystemSteps = [held],
                FollowDecided = SignalSecondWaits,
                DeferredFollowStarting = starting,
            };
            RunningDaemon daemon = await Daemons.StartAsync(temp.Home, options, cancellationToken);
            var client = new DaemonClient(temp.Home);
            await RegisteredProjects.AddAsync(daemon, Project, cancellationToken);
            await Publishing.FlowAsync(daemon, Project, HeldAfterBuild);
            await Publishing.FlowAsync(daemon, Project, AlsoAfterBuild);
            ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Build, then follow from the queue");
            FlowRunView build = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
            await FlowRunDriver.UntilAsync(client, Project, build.Id, "ended");
            return new Chain(daemon, client, parcel.Id, build);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Daemon.DisposeAsync();
        }
    }
}
