using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core.Contours;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.Flows;

namespace Coanda.Tests.Daemon.Triggers;

public sealed class FollowTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A contour that follows the build contour's outcome and ends on one placeholder action, spawning nothing.</summary>
    private static readonly Contour AfterBuild = Ending("after-build", new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 1 });

    /// <summary>A second follower of the same end, naming the build contour in another spelling.</summary>
    private static readonly Contour AlsoAfterBuild = Ending("also-after-build", new ContourFollow { Outcome = "pr-opened", Contour = "Build", DepthCap = 1 });

    /// <summary>A contour that follows an outcome no contour declares.</summary>
    private static readonly Contour AfterNothing = Ending("after-nothing", new ContourFollow { Outcome = "nobody-produces-this", DepthCap = 1 });

    private static Contour Ending(string name, ContourFollow follow) => new()
    {
        Name = name,
        Accepts = ["task"],
        Outcome = "followed",
        Budget = 1m,
        SpawnCap = 1,
        Follow = follow,
        Steps =
        [
            new ContourStep
            {
                Name = "look",
                Action = SoftwareActions.Worktree,
                Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("done"), VisitCap = 1, Target = Contour.End }],
            },
        ],
    };

    [Fact]
    public async Task The_end_of_a_build_flow_starts_its_follower_on_the_same_parcel_once_and_an_unproduced_outcome_starts_nothing()
    {
        using var temp = new TemporaryHome();
        ParcelView parcel;
        FlowView build;
        await using (RunningDaemon first = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None))
        {
            using var client = new DaemonClient(temp.Home);
            await Publishing.ContourAsync(first, Project, AfterBuild);
            await Publishing.ContourAsync(first, Project, AfterNothing);
            parcel = await Flowing.TaskAtBuildAsync(client, Project, "Build, then follow");

            build = await Flowing.BoundAsync(client, Project, parcel.Id);
            (await Flowing.UntilAsync(client, Project, build.Id, "ended")).Outcome.Should().Be("pr-opened");
            FlowView follower = await FollowerOfAsync(client, parcel.Id, build.Id);
            FlowView ended = await Flowing.UntilAsync(client, Project, follower.Id, "ended");

            ended.ContourName.Should().Be(AfterBuild.Name);
            ended.ParcelId.Should().Be(parcel.Id);
            ended.ParentFlowId.Should().Be(build.Id);
            ended.RootFlowId.Should().Be(build.Id);
            ended.Depth.Should().Be(1);
            ended.StartedBy.Should().Be(TriggerLoop.PrincipalFor(build.Id).Id);
            ended.Outcome.Should().Be("followed");

            FlowView root = await Flowing.ShowAsync(client, Project, build.Id);
            root.ParentFlowId.Should().BeNull();
            root.RootFlowId.Should().Be(build.Id);
            root.Depth.Should().Be(0);

            IReadOnlyList<string> lines = FlowRendering.Show(ended);
            lines.Should().Contain($"Parent  {build.Id}").And.Contain("Depth   1");
            FlowRendering.Show(root).Should().NotContain(line => line.StartsWith("Parent", StringComparison.Ordinal) || line.StartsWith("Depth", StringComparison.Ordinal));

            // The follower started from the same end, so the loop has handled it: the contour following an unproduced outcome started nothing.
            (await Flowing.OfParcelAsync(client, Project, parcel.Id)).Select(f => f.ContourName).Should().Equal("build", AfterBuild.Name);
            await first.StopAsync(None);
        }

        await using RunningDaemon second = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var again = new DaemonClient(temp.Home);

        // A later chain's follower ending proves the loop has read past the first chain's end since the restart.
        ParcelView later = await Flowing.TaskAtBuildAsync(again, Project, "A later build");
        FlowView laterBuild = await Flowing.BoundAsync(again, Project, later.Id);
        await Flowing.UntilAsync(again, Project, laterBuild.Id, "ended");
        await Flowing.UntilAsync(again, Project, (await FollowerOfAsync(again, later.Id, laterBuild.Id)).Id, "ended");

        (await Flowing.OfParcelAsync(again, Project, parcel.Id)).Select(f => f.ContourName)
            .Should().Equal(["build", AfterBuild.Name], "a restart starts no second follower of an end already followed");

        await second.StopAsync(None);
    }

    [Fact]
    public async Task Every_contour_following_the_same_end_starts_on_the_parcel_one_after_another()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var client = new DaemonClient(temp.Home);
        await Publishing.ContourAsync(daemon, Project, AfterBuild);
        await Publishing.ContourAsync(daemon, Project, AlsoAfterBuild);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(client, Project, "Build, then follow twice");

        FlowView build = await Flowing.BoundAsync(client, Project, parcel.Id);
        await Flowing.UntilAsync(client, Project, build.Id, "ended");
        List<FlowView> followers = [];
        await Flowing.UntilAsync(
            async () =>
            {
                List<FlowView> flows = await Flowing.OfParcelAsync(client, Project, parcel.Id);
                followers = [.. flows.Where(f => f.ParentFlowId == build.Id && f.Status == "ended")];
                return followers.Count == 2;
            },
            $"both followers of flow {build.Id} end");

        followers.Select(f => f.ContourName).Should().BeEquivalentTo([AfterBuild.Name, AlsoAfterBuild.Name]);
        followers.Should().OnlyContain(f => f.Depth == 1 && f.RootFlowId == build.Id && f.Outcome == "followed");
        (await Flowing.OfParcelAsync(client, Project, parcel.Id)).Should().HaveCount(3, "each follower starts once");

        await daemon.StopAsync(None);
    }

    /// <summary>Waits for the flow a follow starts on the parcel from the end of <paramref name="parentId"/>.</summary>
    private static async Task<FlowView> FollowerOfAsync(DaemonClient client, Guid parcelId, Guid parentId)
    {
        FlowView? follower = null;
        await Flowing.UntilAsync(
            async () =>
            {
                List<FlowView> flows = await Flowing.OfParcelAsync(client, Project, parcelId);
                follower = flows.SingleOrDefault(f => f.ParentFlowId == parentId);
                return follower is not null;
            },
            $"a follower of flow {parentId} starts");
        return follower ?? throw new InvalidOperationException("The wait ended with no follower.");
    }
}
