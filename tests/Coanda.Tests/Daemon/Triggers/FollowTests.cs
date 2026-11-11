using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core.Flows;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Triggers;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;

namespace Coanda.Tests.Daemon.Triggers;

public sealed class FollowTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A flow that follows the build flow's outcome and ends on one placeholder system step, spawning nothing.</summary>
    private static readonly Flow AfterBuild = Ending("after-build", new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 });

    /// <summary>A second follower of the same end, naming the build flow in another spelling.</summary>
    private static readonly Flow AlsoAfterBuild = Ending("also-after-build", new FlowFollow { Outcome = "pr-opened", Flow = "Build", DepthCap = 1 });

    /// <summary>A follower of the same end whose name sorts after the shipped merge's.</summary>
    private static readonly Flow NotifyAfterBuild = Ending("notify-pr", new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 });

    /// <summary>A flow that follows an outcome no flow declares.</summary>
    private static readonly Flow AfterNothing = Ending("after-nothing", new FlowFollow { Outcome = "nobody-produces-this", DepthCap = 1 });

    /// <summary>A follower of the build flow's outcome whose one step is <see cref="RunningSystemStep"/>, which a test holds open.</summary>
    private static readonly Flow HeldAfterBuild = RunningSystemStep.FlowNamed(AfterBuild.Name) with
    {
        Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 },
    };

    private static Flow Ending(string name, FlowFollow follow) => new()
    {
        Name = name,
        Accepts = ["task"],
        Outcome = "followed",
        Budget = 1m,
        SpawnCap = 1,
        Follow = follow,
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
    public async Task The_end_of_a_build_flow_run_starts_its_follower_on_the_same_parcel_once_and_an_unproduced_outcome_starts_nothing()
    {
        using var temp = new TemporaryHome();
        ParcelView parcel;
        FlowRunView build;
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None))
        {
            await RegisteredProjects.AddAsync(first, Project, None);
            using var client = new DaemonClient(temp.Home);
            await Publishing.FlowAsync(first, Project, AfterBuild);
            await Publishing.FlowAsync(first, Project, AfterNothing);
            parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Build, then follow");

            build = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
            Assert.Equal("pr-opened", (await FlowRunDriver.UntilAsync(client, Project, build.Id, "ended")).Outcome);
            FlowRunView follower = await FlowRunDriver.FollowerAsync(client, Project, parcel.Id, build.Id, AfterBuild.Name);
            FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, follower.Id, "ended");

            Assert.Equal(AfterBuild.Name, ended.FlowName);
            Assert.Equal(parcel.Id, ended.ParcelId);
            Assert.Equal(build.Id, ended.ParentFlowRunId);
            Assert.Equal(build.Id, ended.RootFlowRunId);
            Assert.Equal(1, ended.Depth);
            Assert.Equal(TriggerLoop.PrincipalFor(build.Id).Id, ended.StartedBy);
            Assert.Equal("followed", ended.Outcome);

            FlowRunView root = await FlowRunDriver.ShowAsync(client, Project, build.Id);
            Assert.Null(root.ParentFlowRunId);
            Assert.Equal(build.Id, root.RootFlowRunId);
            Assert.Equal(0, root.Depth);

            IReadOnlyList<string> lines = FlowRunRendering.Show(ended);
            Assert.Contains($"Parent  {build.Id}", lines);
            Assert.Contains("Depth   1", lines);
            Assert.DoesNotContain(FlowRunRendering.Show(root), line => line.StartsWith("Parent", StringComparison.Ordinal) || line.StartsWith("Depth", StringComparison.Ordinal));

            // The followers started from the same end, so the loop has handled it: the flow following an unproduced outcome started nothing.
            // The shipped merge closes the parcel, so it starts only once the project's follower releases it.
            await FlowRunDriver.MergedAsync(client, Project, parcel.Id, build.Id);
            Assert.Equal(["build", AfterBuild.Name, "merge"], (await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id)).Select(f => f.FlowName));
            await first.StopAsync(None);
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var again = new DaemonClient(temp.Home);

        // A later chain's follower ending proves the loop has read past the first chain's end since the restart.
        ParcelView later = await FlowRunDriver.TaskAtBuildAsync(again, Project, "A later build");
        FlowRunView laterBuild = await FlowRunDriver.BoundAsync(again, Project, later.Id);
        await FlowRunDriver.UntilAsync(again, Project, laterBuild.Id, "ended");
        await FlowRunDriver.UntilAsync(again, Project, (await FlowRunDriver.FollowerAsync(again, Project, later.Id, laterBuild.Id, AfterBuild.Name)).Id, "ended");
        await FlowRunDriver.MergedAsync(again, Project, later.Id, laterBuild.Id);

        // a restart starts no second follower of an end already followed
        Assert.Equal(["build", AfterBuild.Name, "merge"], (await FlowRunDriver.OfParcelAsync(again, Project, parcel.Id)).Select(f => f.FlowName));

        await second.StopAsync(None);
    }

    [Fact]
    public async Task Every_flow_following_the_same_end_starts_on_the_parcel_one_after_another()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        await Publishing.FlowAsync(daemon, Project, AfterBuild);
        await Publishing.FlowAsync(daemon, Project, AlsoAfterBuild);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Build, then follow twice");

        FlowRunView build = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        await FlowRunDriver.UntilAsync(client, Project, build.Id, "ended");
        List<FlowRunView> followers = [];
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id);
                followers = [.. flowRuns.Where(f => f.ParentFlowRunId == build.Id && f.Status == "ended")];
                return followers.Count == 3;
            },
            $"every follower of flow run {build.Id} ends");

        // The shipped merge follows the same end and closes the parcel, so it starts after both and closes the task.
        Assert.Equal([AfterBuild.Name, AlsoAfterBuild.Name, "merge"], followers.Select(f => f.FlowName));
        Assert.All(followers, f => Assert.True(f.Depth == 1 && f.RootFlowRunId == build.Id));
        Assert.Equal(["followed", "followed", "merged"], followers.Select(f => f.Outcome));
        // each follower starts once
        Assert.Equal(4, (await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id)).Count);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_follower_whose_name_sorts_after_the_closing_merge_still_starts_before_the_merge_closes_the_parcel()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        await Publishing.FlowAsync(daemon, Project, AfterBuild);
        await Publishing.FlowAsync(daemon, Project, NotifyAfterBuild);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Build, then notify after merge's name");

        FlowRunView build = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        await FlowRunDriver.UntilAsync(client, Project, build.Id, "ended");
        List<FlowRunView> followers = [];
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                List<FlowRunView> flowRuns = await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id);
                followers = [.. flowRuns.Where(f => f.ParentFlowRunId == build.Id && f.Status == "ended")];
                return followers.Count == 3;
            },
            $"every follower of flow run {build.Id} ends");

        // "notify-pr" sorts after "merge", but the merge closes the parcel, so every follower that
        // does not close it starts first and none is refused on a closed parcel.
        Assert.Equal([AfterBuild.Name, NotifyAfterBuild.Name, "merge"], followers.Select(f => f.FlowName));
        Assert.Equal(["followed", "followed", "merged"], followers.Select(f => f.Outcome));
        Assert.Equal("closed", (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Stage);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_follower_that_waits_keeps_every_later_follower_of_the_same_end_waiting_behind_it_even_once_the_parcel_is_free()
    {
        using var temp = new TemporaryHome();
        using var stopping = new CancellationTokenSource(TestPatience.Bound);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new RunningSystemStep
        {
            Answer = async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return RunningSystemStep.Done();
            },
        };
        using var client = new DaemonClient(temp.Home);
        Guid parcelId = Guid.Empty;
        int freed = 0;

        // Once the second follower waits behind the held one, the held one ends and frees the parcel
        // before the loop decides on the closing merge.
        async Task FreeAfterSecondWaits(FollowDecision decision, CancellationToken token)
        {
            if (decision.Flow != AlsoAfterBuild.Name || !decision.Waits || Interlocked.Exchange(ref freed, 1) == 1)
            {
                return;
            }

            release.TrySetResult();
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, stopping.Token);
            while ((await client.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", bounded.Token)).LiveFlowRunId is not null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), bounded.Token);
            }
        }

        DaemonOptions options = new()
        {
            Executor = Scripts.CleanBuildExecutor(),
            SystemSteps = [held],
            FollowDecided = FreeAfterSecondWaits,
        };
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, options, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await Publishing.FlowAsync(daemon, Project, HeldAfterBuild);
        await Publishing.FlowAsync(daemon, Project, AlsoAfterBuild);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(client, Project, "Build, then follow with one held");
        parcelId = parcel.Id;

        FlowRunView build = await FlowRunDriver.BoundAsync(client, Project, parcel.Id);
        await FlowRunDriver.UntilAsync(client, Project, build.Id, "ended");
        FlowRunView merge = await FlowRunDriver.MergedAsync(client, Project, parcel.Id, build.Id);

        List<FlowRunView> followers = [.. (await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id)).Where(f => f.ParentFlowRunId == build.Id)];
        FlowRunView? waited = followers.SingleOrDefault(f => f.FlowName == AlsoAfterBuild.Name);
        Assert.NotNull(waited);
        Assert.Equal("ended", waited.Status);
        Assert.Equal("followed", waited.Outcome);
        // An ended flow run was last updated by its end.
        Assert.True(waited.UpdatedAt <= merge.StartedAt, $"'{AlsoAfterBuild.Name}' ended at {waited.UpdatedAt}, after the merge started at {merge.StartedAt}.");
        Assert.Equal([AfterBuild.Name, AlsoAfterBuild.Name, "merge"], followers.OrderBy(f => f.StartedAt).Select(f => f.FlowName));
        Assert.All(followers, f => Assert.Equal("ended", f.Status));
        Assert.Equal("closed", (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Stage);

        await daemon.StopAsync(None);
    }
}
