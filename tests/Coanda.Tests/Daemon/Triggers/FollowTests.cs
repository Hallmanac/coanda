using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core.Flows;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;

namespace Coanda.Tests.Daemon.Triggers;

public sealed class FollowTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A flow that follows the build flow's outcome and ends on one placeholder action, spawning nothing.</summary>
    private static readonly Flow AfterBuild = Ending("after-build", new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 });

    /// <summary>A second follower of the same end, naming the build flow in another spelling.</summary>
    private static readonly Flow AlsoAfterBuild = Ending("also-after-build", new FlowFollow { Outcome = "pr-opened", Flow = "Build", DepthCap = 1 });

    /// <summary>A follower of the same end whose name sorts after the shipped merge's.</summary>
    private static readonly Flow NotifyAfterBuild = Ending("notify-pr", new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 });

    /// <summary>A flow that follows an outcome no flow declares.</summary>
    private static readonly Flow AfterNothing = Ending("after-nothing", new FlowFollow { Outcome = "nobody-produces-this", DepthCap = 1 });

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
                Action = SoftwareActions.Worktree,
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
            (await FlowRunDriver.UntilAsync(client, Project, build.Id, "ended")).Outcome.Should().Be("pr-opened");
            FlowRunView follower = await FlowRunDriver.FollowerAsync(client, Project, parcel.Id, build.Id, AfterBuild.Name);
            FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, follower.Id, "ended");

            ended.FlowName.Should().Be(AfterBuild.Name);
            ended.ParcelId.Should().Be(parcel.Id);
            ended.ParentFlowRunId.Should().Be(build.Id);
            ended.RootFlowRunId.Should().Be(build.Id);
            ended.Depth.Should().Be(1);
            ended.StartedBy.Should().Be(TriggerLoop.PrincipalFor(build.Id).Id);
            ended.Outcome.Should().Be("followed");

            FlowRunView root = await FlowRunDriver.ShowAsync(client, Project, build.Id);
            root.ParentFlowRunId.Should().BeNull();
            root.RootFlowRunId.Should().Be(build.Id);
            root.Depth.Should().Be(0);

            IReadOnlyList<string> lines = FlowRunRendering.Show(ended);
            lines.Should().Contain($"Parent  {build.Id}").And.Contain("Depth   1");
            FlowRunRendering.Show(root).Should().NotContain(line => line.StartsWith("Parent", StringComparison.Ordinal) || line.StartsWith("Depth", StringComparison.Ordinal));

            // The followers started from the same end, so the loop has handled it: the flow following an unproduced outcome started nothing.
            // The shipped merge closes the parcel, so it starts only once the project's follower releases it.
            await FlowRunDriver.MergedAsync(client, Project, parcel.Id, build.Id);
            (await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id)).Select(f => f.FlowName).Should().Equal("build", AfterBuild.Name, "merge");
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

        (await FlowRunDriver.OfParcelAsync(again, Project, parcel.Id)).Select(f => f.FlowName)
            .Should().Equal(["build", AfterBuild.Name, "merge"], "a restart starts no second follower of an end already followed");

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
        followers.Select(f => f.FlowName).Should().Equal([AfterBuild.Name, AlsoAfterBuild.Name, "merge"]);
        followers.Should().OnlyContain(f => f.Depth == 1 && f.RootFlowRunId == build.Id);
        followers.Select(f => f.Outcome).Should().Equal(["followed", "followed", "merged"]);
        (await FlowRunDriver.OfParcelAsync(client, Project, parcel.Id)).Should().HaveCount(4, "each follower starts once");

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
        followers.Select(f => f.FlowName).Should().Equal([AfterBuild.Name, NotifyAfterBuild.Name, "merge"]);
        followers.Select(f => f.Outcome).Should().Equal(["followed", "followed", "merged"]);
        (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).State.Should().Be("closed");

        await daemon.StopAsync(None);
    }
}
