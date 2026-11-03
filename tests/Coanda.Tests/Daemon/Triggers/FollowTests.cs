using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core.Flows;
using Coanda.Core.Software;
using Coanda.Daemon.Api;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.Flights;

namespace Coanda.Tests.Daemon.Triggers;

public sealed class FollowTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A flow that follows the build flow's outcome and ends on one placeholder action, spawning nothing.</summary>
    private static readonly FlowDefinition AfterBuild = Ending("after-build", new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 1 });

    /// <summary>A flow that follows an outcome no flow declares.</summary>
    private static readonly FlowDefinition AfterNothing = Ending("after-nothing", new FlowFollow { Outcome = "nobody-produces-this", DepthCap = 1 });

    private static FlowDefinition Ending(string name, FlowFollow follow) => new()
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
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = FlowDefinition.End }],
            },
        ],
    };

    [Fact]
    public async Task The_end_of_the_build_flow_starts_its_follower_on_the_same_parcel_once_and_an_unproduced_outcome_starts_nothing()
    {
        using var temp = new TemporaryHome();
        ParcelView parcel;
        FlightView build;
        await using (RunningDaemon first = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None))
        {
            using var client = new DaemonClient(temp.Home);
            await Publishing.FlowAsync(first, Project, AfterBuild);
            await Publishing.FlowAsync(first, Project, AfterNothing);
            parcel = await Flying.TaskAtBuildAsync(client, Project, "Build, then follow");

            build = await Flying.BoundAsync(client, Project, parcel.Id);
            (await Flying.UntilAsync(client, Project, build.Id, "ended")).Outcome.Should().Be("pr-opened");
            FlightView follower = await FollowerOfAsync(client, parcel.Id, build.Id);
            FlightView ended = await Flying.UntilAsync(client, Project, follower.Id, "ended");

            ended.Flow.Should().Be(AfterBuild.Name);
            ended.ParcelId.Should().Be(parcel.Id);
            ended.ParentFlightId.Should().Be(build.Id);
            ended.RootFlightId.Should().Be(build.Id);
            ended.Depth.Should().Be(1);
            ended.StartedBy.Should().Be(TriggerLoop.PrincipalFor(build.Id).Id);
            ended.Outcome.Should().Be("followed");

            FlightView root = await Flying.ShowAsync(client, Project, build.Id);
            root.ParentFlightId.Should().BeNull();
            root.RootFlightId.Should().Be(build.Id);
            root.Depth.Should().Be(0);

            IReadOnlyList<string> lines = FlightRendering.Show(ended);
            lines.Should().Contain($"Parent  {build.Id}").And.Contain("Depth   1");
            FlightRendering.Show(root).Should().NotContain(line => line.StartsWith("Parent", StringComparison.Ordinal) || line.StartsWith("Depth", StringComparison.Ordinal));

            // The follower started from the same end, so the loop has handled it: the flow following an unproduced outcome started nothing.
            (await Flying.OfParcelAsync(client, Project, parcel.Id)).Select(f => f.Flow).Should().Equal("build", AfterBuild.Name);
            await first.StopAsync(None);
        }

        await using RunningDaemon second = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Executor = Scripts.CleanBuildExecutor() }, None);
        using var again = new DaemonClient(temp.Home);

        // A later chain's follower ending proves the loop has read past the first chain's end since the restart.
        ParcelView later = await Flying.TaskAtBuildAsync(again, Project, "A later build");
        FlightView laterBuild = await Flying.BoundAsync(again, Project, later.Id);
        await Flying.UntilAsync(again, Project, laterBuild.Id, "ended");
        await Flying.UntilAsync(again, Project, (await FollowerOfAsync(again, later.Id, laterBuild.Id)).Id, "ended");

        (await Flying.OfParcelAsync(again, Project, parcel.Id)).Select(f => f.Flow)
            .Should().Equal(["build", AfterBuild.Name], "a restart starts no second follower of an end already followed");

        await second.StopAsync(None);
    }

    /// <summary>Waits for the flight a follow starts on the parcel from the end of <paramref name="parentId"/>.</summary>
    private static async Task<FlightView> FollowerOfAsync(DaemonClient client, Guid parcelId, Guid parentId)
    {
        FlightView? follower = null;
        await Flying.UntilAsync(
            async () =>
            {
                List<FlightView> flights = await Flying.OfParcelAsync(client, Project, parcelId);
                follower = flights.SingleOrDefault(f => f.ParentFlightId == parentId);
                return follower is not null;
            },
            $"a follower of flight {parentId} starts");
        return follower ?? throw new InvalidOperationException("The wait ended with no follower.");
    }
}
