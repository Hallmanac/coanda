using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Cli;
using Coanda.Core.Contours;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Flows;
using Coanda.Tests.Daemon.GitHub;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>
/// The merge contour as the daemon runs it after a build: its waiting lap, a close its parcel's gate
/// refuses and the resume that then closes it, and a project whose QA contour sits between the
/// build and its own merge. A lap test runs a project contour of the shipped merge's shape with a
/// timer of a few milliseconds and smaller caps; the shipped contour's twenty-minute floor is checked from its data.
/// </summary>
public sealed class MergeFlowTests
{
    private const string Project = "alpha";
    private const int LapCap = 3;
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The shipped merge contour's shape with a short timer: <see cref="LapCap"/> laps, and a merged cap five above it.</summary>
    private static readonly Contour QuickLap = ShippedContours.Merge with
    {
        Steps =
        [
            new ContourStep
            {
                Name = "gated-merge",
                Action = SoftwareActions.GatedMerge,
                Transitions = [On("merged", Contour.End, LapCap + 5), On("pending", "wait-for-checks", LapCap)],
            },
            new ContourStep
            {
                Name = "wait-for-checks",
                Action = CoreActions.Timer,
                Arguments = new Dictionary<string, JsonElement> { ["seconds"] = ContourValue.Of(0.01) },
                Transitions = [On("done", "gated-merge", LapCap)],
            },
        ],
    };

    /// <summary>A QA contour that follows the build's pull request: one review that ends on clean and pauses on not-clean.</summary>
    private static readonly Contour Qa = new()
    {
        Name = "qa",
        Accepts = ["task"],
        Outcome = "qa-passed",
        Budget = 2m,
        SpawnCap = 1,
        Follow = new ContourFollow { Outcome = "pr-opened", Contour = "build", DepthCap = 3 },
        Steps =
        [
            new ContourStep
            {
                Name = "qa",
                Agent = ShippedAgents.ReviewerAdversarial.Name,
                AnswerSchema = "review",
                Transitions = [On("clean", Contour.End, 1), On("not-clean", Contour.Pause, 1)],
            },
        ],
    };

    /// <summary>The project's own merge, named as the shipped one so it replaces it there, following QA's outcome rather than the build's.</summary>
    private static readonly Contour MergeAfterQa = ShippedContours.Merge with
    {
        Follow = new ContourFollow { Outcome = "qa-passed", Contour = "qa", DepthCap = 3 },
    };

    private static ContourTransition On(string verdict, string target, int cap) =>
        new() { Field = "verdict", Value = ContourValue.Of(verdict), VisitCap = cap, Target = target };

    [Fact]
    public async Task A_merge_waits_through_its_lap_while_checks_run_and_merges_once_they_pass()
    {
        var github = new FakeGitHub();
        int reads = 0;
        github.On(r => r.IsCheckRuns, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.CheckRuns(
            [Interlocked.Increment(ref reads) <= 2
                ? FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "in_progress", null)
                : FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "completed", "success")],
            total: 1)));
        await using Harness harness = await Harness.StartAsync(github, QuickLap);

        (FlowView build, FlowView merge) = await harness.BuildThenMergeAsync("Wait for CI", "ended");

        merge.Steps.Select(s => s.Step).Should().Equal("gated-merge", "wait-for-checks", "gated-merge", "wait-for-checks", "gated-merge");
        merge.Steps[0].Result.GetValueOrDefault().GetProperty("verdict").GetString().Should().Be("pending");
        merge.Steps[0].Result.GetValueOrDefault().GetProperty("reason").GetString().Should().Contain("is in_progress");
        github.Requests.Should().ContainSingle(r => r.IsMerge);
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
    }

    [Fact]
    public async Task A_merge_still_pending_once_its_lap_cap_is_spent_pauses_and_merges_nothing()
    {
        var github = new FakeGitHub();
        github.On(r => r.IsCheckRuns, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.CheckRuns(
            [FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "queued", null)], total: 1)));
        await using Harness harness = await Harness.StartAsync(github, QuickLap);

        (FlowView build, FlowView merge) = await harness.BuildThenMergeAsync("CI never finishes", "paused");

        merge.PauseReason.Should().Be($"Step 'gated-merge' reached the visit cap of {LapCap} on its transition to 'wait-for-checks'.");
        merge.Steps.Count(s => s.Step == "gated-merge").Should().Be(LapCap + 1);
        github.Requests.Should().NotContain(r => r.IsMerge);
        ParcelView parcel = await harness.ParcelAsync(build.ParcelId);
        parcel.State.Should().Be("review");
        parcel.LiveFlowId.Should().Be(merge.Id);
    }

    [Fact]
    public async Task A_merge_on_the_laps_last_visit_ends_the_flow_rather_than_pausing_on_a_visit_cap()
    {
        var github = new FakeGitHub();
        int reads = 0;
        github.On(r => r.IsCheckRuns, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.CheckRuns(
            [Interlocked.Increment(ref reads) <= LapCap
                ? FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "in_progress", null)
                : FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "completed", "success")],
            total: 1)));
        await using Harness harness = await Harness.StartAsync(github, QuickLap);

        (FlowView build, FlowView merge) = await harness.BuildThenMergeAsync("CI passes at the last moment", "ended");

        merge.Steps.Count(s => s.Step == "gated-merge").Should().Be(LapCap + 1, "the merge came on the visit a pending answer would have paused");
        merge.Outcome.Should().Be("merged");
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
    }

    [Fact]
    public async Task A_close_its_gate_refuses_pauses_the_merge_holding_the_open_task_and_the_resume_closes_it_without_a_second_merge_request()
    {
        var github = new FakeGitHub();
        var hold = new HoldingGate();
        await using Harness harness = await Harness.StartAsync(github, project: null, hold);
        await harness.PublishTypeAsync(ShippedTypes.Task with
        {
            GateBindings = [.. ShippedTypes.Task.GateBindings, new GateBinding("review", ShippedTypes.Closed, HoldingGate.GateName)],
        });

        (FlowView build, FlowView merge) = await harness.BuildThenMergeAsync("Closed only when released", "paused");

        merge.PauseReason.Should().StartWith("Flow could not go on from step 'gated-merge': ")
            .And.Contain($"of contour 'merge' reached its end, but its parcel could not be closed as 'completed'")
            .And.Contain($"The gate '{HoldingGate.GateName}' refused the move from 'review' to 'closed': {HoldingGate.Reason}");
        merge.Lever.Should().Be(FlowLevers.PerformAgain("gated-merge"));
        ParcelView held = await harness.ParcelAsync(build.ParcelId);
        held.State.Should().Be("review");
        held.Resolution.Should().BeNull();
        held.LiveFlowId.Should().Be(merge.Id, "the end, the release, and the close were recorded together or not at all");
        github.Requests.Should().ContainSingle(r => r.IsMerge);

        hold.Holding = false;
        await Flowing.ResumeAsync(harness.Client, Project, merge.Id, "the gate no longer holds");
        FlowView ended = await Flowing.UntilAsync(harness.Client, Project, merge.Id, "ended");

        ended.Outcome.Should().Be("merged");
        ended.Steps.Where(s => s.Step == "gated-merge").Should().HaveCount(2, "the resume performs gated-merge again as a new visit");
        github.Requests.Should().ContainSingle(r => r.IsMerge, "the pull request is already merged, so the resume makes no second merge request");
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
    }

    [Fact]
    public async Task In_a_project_whose_qa_contour_follows_the_build_the_merge_follows_qa_and_merges_only_after_qa_ended()
    {
        var github = new FakeGitHub();
        DateTimeOffset? mergedAt = null;
        github.On(r => r.IsMerge, _ =>
        {
            mergedAt = DateTimeOffset.UtcNow;
            return FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["sha"] = FakeGitHub.MergeCommit, ["merged"] = true });
        });
        github.On(r => r.IsPullRequestRead, r => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, merged: mergedAt is not null)));
        var executor = new GatedExecutor(QaScript("clean")) { FreeCollects = 3 };
        await using Harness harness = await Harness.StartAsync(github, project: null, executor: executor);
        await Publishing.ContourAsync(harness.Daemon, Project, Qa);
        await Publishing.ContourAsync(harness.Daemon, Project, MergeAfterQa);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(harness.Client, Project, "QA first");
        FlowView build = await Flowing.BoundAsync(harness.Client, Project, parcel.Id);
        await Flowing.UntilAsync(harness.Client, Project, build.Id, "ended");

        // QA holds its review open: the build's end started QA alone, and nothing has asked GitHub to merge.
        await executor.Holding.WaitAsync(TimeSpan.FromSeconds(15));
        FlowView qa = await Flowing.FollowerAsync(harness.Client, Project, parcel.Id, build.Id, Qa.Name);
        (await Flowing.OfParcelAsync(harness.Client, Project, parcel.Id)).Select(f => f.ContourName).Should().Equal("build", "qa");
        github.Requests.Should().NotContain(r => r.IsMerge || r.IsPullRequestRead);

        executor.Open();
        qa = await Flowing.UntilAsync(harness.Client, Project, qa.Id, "ended");
        FlowView merge = await Flowing.FollowerAsync(harness.Client, Project, parcel.Id, qa.Id, "merge");
        merge = await Flowing.UntilAsync(harness.Client, Project, merge.Id, "ended");

        merge.ContourHash.Should().Be(MergeAfterQa.Hash, "the project's own merge replaces the shipped one by name");
        merge.Depth.Should().Be(2);
        merge.RootFlowId.Should().Be(build.Id);
        (await Flowing.OfParcelAsync(harness.Client, Project, parcel.Id)).Select(f => f.ContourName).Should().Equal("build", "qa", "merge");
        github.Requests.Should().ContainSingle(r => r.IsMerge);
        mergedAt.Should().NotBeNull().And.BeOnOrAfter(qa.UpdatedAt, "the merge request was made after the QA flow ended");
        await harness.ExpectClosedAsync(parcel.Id, merge.Id);
    }

    [Fact]
    public async Task A_qa_flow_whose_table_pauses_it_on_a_failing_answer_starts_no_merge_and_leaves_the_task_open_in_review()
    {
        var github = new FakeGitHub();
        await using Harness harness = await Harness.StartAsync(github, project: null, executor: QaScript("not-clean"));
        await Publishing.ContourAsync(harness.Daemon, Project, Qa);
        await Publishing.ContourAsync(harness.Daemon, Project, MergeAfterQa);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(harness.Client, Project, "QA fails");
        FlowView build = await Flowing.BoundAsync(harness.Client, Project, parcel.Id);
        await Flowing.UntilAsync(harness.Client, Project, build.Id, "ended");
        FlowView qa = await Flowing.FollowerAsync(harness.Client, Project, parcel.Id, build.Id, Qa.Name);

        qa = await Flowing.UntilAsync(harness.Client, Project, qa.Id, "paused");

        qa.PauseReason.Should().Contain("pauses on its answer");
        await harness.ExpectNoMergeAsync(parcel.Id, ["build", "qa"]);
    }

    [Fact]
    public async Task A_qa_flow_a_person_stops_starts_no_merge_and_leaves_the_task_open_in_review()
    {
        var github = new FakeGitHub();
        var executor = new GatedExecutor(QaScript("clean")) { FreeCollects = 3 };
        await using Harness harness = await Harness.StartAsync(github, project: null, executor: executor);
        await Publishing.ContourAsync(harness.Daemon, Project, Qa);
        await Publishing.ContourAsync(harness.Daemon, Project, MergeAfterQa);
        ParcelView parcel = await Flowing.TaskAtBuildAsync(harness.Client, Project, "QA stopped");
        FlowView build = await Flowing.BoundAsync(harness.Client, Project, parcel.Id);
        await Flowing.UntilAsync(harness.Client, Project, build.Id, "ended");
        await executor.Holding.WaitAsync(TimeSpan.FromSeconds(15));
        FlowView qa = await Flowing.FollowerAsync(harness.Client, Project, parcel.Id, build.Id, Qa.Name);

        await Flowing.StopAsync(harness.Client, Project, qa.Id, "QA is not wanted on this one");

        (await Flowing.UntilAsync(harness.Client, Project, qa.Id, "stopped")).Status.Should().Be("stopped");
        await harness.ExpectNoMergeAsync(parcel.Id, ["build", "qa"]);
    }

    /// <summary>The clean build path, then the QA review answering <paramref name="qaVerdict"/>.</summary>
    private static ScriptedExecutor QaScript(string qaVerdict) => new(new Dictionary<string, IReadOnlyList<ScriptEntry>>
    {
        ["build"] = [Scripts.Answer("done", 1)],
        ["reviewer-adversarial"] = [Scripts.Answer("clean", 2), Scripts.Answer(qaVerdict, 4)],
        ["reviewer-conformance"] = [Scripts.Answer("clean", 3)],
    });

    /// <summary>A gate on the close that refuses while <see cref="Holding"/>, so a test can release it.</summary>
    private sealed class HoldingGate : IGate
    {
        public const string GateName = "hold-close";
        public const string Reason = "the test holds the close";

        public volatile bool Holding = true;

        public string Name => GateName;

        public GateVerdict Check(Parcel parcel, ParcelType type, string fromState, string toState) =>
            Holding
                ? GateVerdict.Fail(Reason)
                : GateVerdict.Pass;
    }

    /// <summary>A running daemon with the alpha project registered, a fake GitHub, and a client.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TemporaryHome temp;

        private Harness(TemporaryHome temp, RunningDaemon daemon, FakeGitHub github)
        {
            this.temp = temp;
            Daemon = daemon;
            GitHub = github;
            Client = new DaemonClient(temp.Home);
        }

        public RunningDaemon Daemon { get; }

        public FakeGitHub GitHub { get; }

        public DaemonClient Client { get; }

        public static async Task<Harness> StartAsync(FakeGitHub github, Contour? project, IGate? gate = null, IAgentExecutor? executor = null)
        {
            var temp = new TemporaryHome();
            RunningDaemon daemon = await Daemons.StartAsync(
                temp.Home,
                new DaemonOptions { Executor = executor ?? Scripts.CleanBuildExecutor(), GitHubHandler = github.Handler, Gates = gate is null ? [] : [gate] },
                None);
            await RegisteredProjects.AddAsync(daemon, Project, None);
            if (project is not null)
            {
                await Publishing.ContourAsync(daemon, Project, project);
            }

            return new Harness(temp, daemon, github);
        }

        public async Task PublishTypeAsync(ParcelType type)
        {
            DefinitionService definitions = Daemon.Services.GetRequiredService<DefinitionService>();
            Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, Project, type, null, None);
            await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        }

        /// <summary>Runs a task's build to its end and waits for the merge that follows to reach <paramref name="status"/>.</summary>
        public async Task<(FlowView Build, FlowView Merge)> BuildThenMergeAsync(string title, string status)
        {
            ParcelView parcel = await Flowing.TaskAtBuildAsync(Client, Project, title);
            FlowView build = await Flowing.BoundAsync(Client, Project, parcel.Id);
            build = await Flowing.UntilAsync(Client, Project, build.Id, "ended");
            FlowView merge = await Flowing.FollowerAsync(Client, Project, parcel.Id, build.Id, "merge");
            return (build, await Flowing.UntilAsync(Client, Project, merge.Id, status));
        }

        public Task<ParcelView> ParcelAsync(Guid parcelId) =>
            Client.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", None);

        /// <summary>The task closed as completed, released, by the merge flow's own principal, with a reason naming the flow and its contour.</summary>
        public async Task ExpectClosedAsync(Guid parcelId, Guid mergeId)
        {
            ParcelView parcel = await ParcelAsync(parcelId);
            parcel.State.Should().Be("closed");
            parcel.Resolution.Should().Be("completed");
            parcel.LiveFlowId.Should().BeNull();
            parcel.CloseReason.Should().Contain($"of contour 'merge' reached its end");

            await using IQuerySession session = Daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Project);
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: None);
            ParcelClosed closed = events.Select(e => e.Data).OfType<ParcelClosed>().Should().ContainSingle().Subject;
            closed.ClosedBy.Should().Be(FlowRunner.PrincipalFor(mergeId).Id);
            object[] tail = [.. events.TakeLast(2).Select(e => e.Data)];
            tail[0].Should().BeOfType<ParcelFlowReleased>().Which.FlowId.Should().Be(mergeId);
            tail[1].Should().BeSameAs(closed);
            events.TakeLast(2).Select(e => e.Version).Should().BeInAscendingOrder();
        }

        /// <summary>
        /// No merge flow started and no merge was asked for, and the task is open in review. A later
        /// task's bound build proves the trigger loop has read past everything this one recorded.
        /// </summary>
        public async Task ExpectNoMergeAsync(Guid parcelId, string[] flows)
        {
            ParcelView later = await Flowing.TaskAtBuildAsync(Client, Project, "A later task");
            await Flowing.BoundAsync(Client, Project, later.Id);

            (await Flowing.OfParcelAsync(Client, Project, parcelId)).Select(f => f.ContourName).Should().Equal(flows);
            GitHub.Requests.Should().NotContain(r => r.IsMerge);
            ParcelView parcel = await ParcelAsync(parcelId);
            parcel.State.Should().Be("review");
            parcel.Resolution.Should().BeNull();
        }

        public async ValueTask DisposeAsync()
        {
            await Daemon.StopAsync(None);
            await Daemon.DisposeAsync();
            Client.Dispose();
            temp.Dispose();
        }
    }
}
