using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Cli;
using Coanda.Core.Flows;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The merge flow as the daemon runs it after a build: its waiting lap, its rebase lap, a close
/// its parcel's gate refuses and the resume that then closes it, and a project whose QA flow sits
/// between the build and its own merge. A lap test runs a project flow of the shipped merge's
/// shape with a timer of a few milliseconds and smaller caps; the shipped flow's twenty-minute
/// floor and its rebase lap's route and caps are checked from its data.
/// </summary>
public sealed class MergeFlowRunTests
{
    private const string Project = "alpha";
    private const int LapCap = 3;
    private const int RebaseLaps = 2;
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The shipped merge flow's shape with a short timer: <see cref="LapCap"/> laps, and a merged cap five above it.</summary>
    private static readonly Flow QuickLap = SoftwareFlows.Merge with
    {
        Steps =
        [
            new FlowStep
            {
                Name = "gated-merge",
                SystemStep = SoftwareSteps.GatedMerge,
                Transitions = [On("merged", Flow.End, LapCap + 5), On("pending", "wait-for-checks", LapCap)],
            },
            new FlowStep
            {
                Name = "wait-for-checks",
                SystemStep = CoreSystemSteps.Timer,
                Arguments = new Dictionary<string, JsonElement> { ["seconds"] = FlowValue.Of(0.01) },
                Transitions = [On("done", "gated-merge", LapCap)],
            },
        ],
    };

    /// <summary>
    /// The shipped merge flow's shape, rebase lap included, with a short timer: six inspections,
    /// <see cref="RebaseLaps"/> rebases, and a merged cap five above the pending cap.
    /// </summary>
    private static readonly Flow QuickRebaseLap = SoftwareFlows.Merge with
    {
        Steps =
        [
            new FlowStep
            {
                Name = "gated-merge",
                SystemStep = SoftwareSteps.GatedMerge,
                Transitions = [On("merged", Flow.End, 11), On("pending", "wait-for-checks", 6), On("behind", "worktree", 6)],
            },
            new FlowStep
            {
                Name = "wait-for-checks",
                SystemStep = CoreSystemSteps.Timer,
                Arguments = new Dictionary<string, JsonElement> { ["seconds"] = FlowValue.Of(0.01) },
                Transitions = [On("done", "gated-merge", 6)],
            },
            new FlowStep { Name = "worktree", SystemStep = SoftwareSteps.Worktree, Transitions = [On("done", "rebase", RebaseLaps)] },
            new FlowStep { Name = "rebase", SystemStep = SoftwareSteps.Rebase, Transitions = [On("done", "wait-for-checks", RebaseLaps)] },
        ],
    };

    /// <summary>The commit the scripted runner's rebase makes of the build's pushed head onto the primary branch.</summary>
    private static readonly string Rebased = ScriptedCommandRunner.RebasedOnto(ScriptedCommandRunner.PrimaryHead, ScriptedCommandRunner.PrimaryHead);

    /// <summary>A QA flow that follows the build's pull request: one review that ends on clean and pauses on not-clean.</summary>
    private static readonly Flow Qa = new()
    {
        Name = "qa",
        Accepts = ["task"],
        Outcome = "qa-passed",
        Budget = 2m,
        SpawnCap = 1,
        Follow = new FlowFollow { Outcome = "pr-opened", Flow = "build", DepthCap = 3 },
        Steps =
        [
            new FlowStep
            {
                Name = "qa",
                Agent = SoftwareAgents.ReviewerAdversarial.Name,
                AnswerSchema = "review",
                Transitions = [On("clean", Flow.End, 1), On("not-clean", Flow.Pause, 1)],
            },
        ],
    };

    /// <summary>The project's own merge, named as the shipped one so it replaces it there, following QA's outcome rather than the build's.</summary>
    private static readonly Flow MergeAfterQa = SoftwareFlows.Merge with
    {
        Follow = new FlowFollow { Outcome = "qa-passed", Flow = "qa", DepthCap = 3 },
    };

    private static FlowTransition On(string verdict, string target, int cap) =>
        new() { Field = "verdict", Value = FlowValue.Of(verdict), VisitCap = cap, Target = target };

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

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("Wait for CI", "ended");

        Assert.Equal(["gated-merge", "wait-for-checks", "gated-merge", "wait-for-checks", "gated-merge"], merge.Steps.Select(s => s.Step));
        Assert.Equal("pending", merge.Steps[0].Result.GetValueOrDefault().GetProperty("verdict").GetString());
        Assert.Contains("is in_progress", merge.Steps[0].Result.GetValueOrDefault().GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Single(github.Requests, r => r.IsMerge);
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
    }

    [Fact]
    public async Task A_merge_still_pending_once_its_lap_cap_is_spent_pauses_and_merges_nothing()
    {
        var github = new FakeGitHub();
        github.On(r => r.IsCheckRuns, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.CheckRuns(
            [FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "queued", null)], total: 1)));
        await using Harness harness = await Harness.StartAsync(github, QuickLap);

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("CI never finishes", "paused");

        Assert.Equal($"Step 'gated-merge' reached the visit cap of {LapCap} on its transition to 'wait-for-checks'.", merge.PauseReason);
        Assert.Equal(LapCap + 1, merge.Steps.Count(s => s.Step == "gated-merge"));
        Assert.DoesNotContain(github.Requests, r => r.IsMerge);
        ParcelView parcel = await harness.ParcelAsync(build.ParcelId);
        Assert.Equal("review", parcel.Stage);
        Assert.Equal(merge.Id, parcel.LiveFlowRunId);
    }

    [Fact]
    public async Task A_merge_on_the_laps_last_visit_ends_the_flow_run_rather_than_pausing_on_a_visit_cap()
    {
        var github = new FakeGitHub();
        int reads = 0;
        github.On(r => r.IsCheckRuns, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.CheckRuns(
            [Interlocked.Increment(ref reads) <= LapCap
                ? FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "in_progress", null)
                : FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "completed", "success")],
            total: 1)));
        await using Harness harness = await Harness.StartAsync(github, QuickLap);

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("CI passes at the last moment", "ended");

        // the merge came on the visit a pending answer would have paused
        Assert.Equal(LapCap + 1, merge.Steps.Count(s => s.Step == "gated-merge"));
        Assert.Equal("merged", merge.Outcome);
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
    }

    [Fact]
    public async Task A_behind_pull_request_is_rebased_its_checks_run_on_the_new_head_and_it_is_merged_expecting_the_rebased_commit()
    {
        var runner = new ScriptedCommandRunner();
        var github = new FakeGitHub();
        var pr = new RemotePullRequest(runner, github);
        int newHeadReads = 0;
        pr.BehindWhen = (head, _, _) => head == ScriptedCommandRunner.PrimaryHead;
        pr.ChecksOn = head => head == Rebased && Interlocked.Increment(ref newHeadReads) == 1
            ? "in_progress"
            : "success";
        await using Harness harness = await Harness.StartAsync(github, QuickRebaseLap, runner: runner);

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("Behind main", "ended", pr);

        Assert.Equal(["gated-merge", "worktree", "rebase", "wait-for-checks", "gated-merge", "wait-for-checks", "gated-merge"], merge.Steps.Select(s => s.Step));
        Assert.Equal("behind", merge.Steps[0].Result.GetValueOrDefault().GetProperty("verdict").GetString());
        Assert.Equal(Rebased, merge.Steps[2].Result.GetValueOrDefault().GetProperty("head").GetString());
        Assert.Equal("pending", merge.Steps[4].Result.GetValueOrDefault().GetProperty("verdict").GetString());
        Assert.Equal(Rebased, (string?)JsonNode.Parse(github.Requests.Single(r => r.IsMerge).Body ?? "{}")?["sha"]);
        Assert.Contains($"push --force-with-lease=refs/heads/{pr.Branch}:{ScriptedCommandRunner.PrimaryHead} origin {Rebased}:refs/heads/{pr.Branch}", runner.GitCalls);
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
        await ExpectOnlyTheMergesOwnEndRemovedItsWorktreeAsync(runner, harness, build, merge);
    }

    [Fact]
    public async Task A_head_current_at_its_first_inspection_and_behind_at_a_later_one_is_rebased()
    {
        var runner = new ScriptedCommandRunner();
        var github = new FakeGitHub();
        var pr = new RemotePullRequest(runner, github);
        pr.BehindWhen = (head, inspection, _) => head == ScriptedCommandRunner.PrimaryHead && inspection >= 2;
        pr.ChecksOn = head => head == ScriptedCommandRunner.PrimaryHead
            ? "in_progress"
            : "success";
        await using Harness harness = await Harness.StartAsync(github, QuickRebaseLap, runner: runner);

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("Falls behind while CI runs", "ended", pr);

        Assert.Equal(["gated-merge", "wait-for-checks", "gated-merge", "worktree", "rebase", "wait-for-checks", "gated-merge"], merge.Steps.Select(s => s.Step));
        Assert.Equal(Rebased, (string?)JsonNode.Parse(github.Requests.Single(r => r.IsMerge).Body ?? "{}")?["sha"]);
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
    }

    [Fact]
    public async Task A_head_found_behind_only_at_the_comparison_right_before_the_merge_request_is_rebased()
    {
        var runner = new ScriptedCommandRunner();
        var github = new FakeGitHub();
        var pr = new RemotePullRequest(runner, github);
        pr.BehindWhen = (head, _, comparison) => head == ScriptedCommandRunner.PrimaryHead && comparison == 2;
        await using Harness harness = await Harness.StartAsync(github, QuickRebaseLap, runner: runner);

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("Main moves at the last moment", "ended", pr);

        Assert.Equal(["gated-merge", "worktree", "rebase", "wait-for-checks", "gated-merge"], merge.Steps.Select(s => s.Step));
        // both comparisons ran on each visit
        Assert.Equal(4, github.Requests.Where(r => r.IsCompare).Count());
        Assert.Equal(Rebased, (string?)JsonNode.Parse(github.Requests.Single(r => r.IsMerge).Body ?? "{}")?["sha"]);
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
    }

    [Fact]
    public async Task A_head_still_behind_once_the_rebase_laps_are_spent_pauses_on_the_worktree_steps_cap_before_a_further_rebase()
    {
        var runner = new ScriptedCommandRunner();
        var github = new FakeGitHub();
        var pr = new RemotePullRequest(runner, github);
        pr.BehindWhen = (_, _, _) => true;
        await using Harness harness = await Harness.StartAsync(github, QuickRebaseLap, runner: runner);

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("Main never stops moving", "paused", pr);

        Assert.Equal($"Step 'worktree' reached the visit cap of {RebaseLaps} on its transition to 'rebase'.", merge.PauseReason);
        Assert.Equal(RebaseLaps, merge.Steps.Count(s => s.Step == "rebase"));
        Assert.Equal(RebaseLaps + 1, merge.Steps.Count(s => s.Step == "worktree"));
        // the build's push and one per rebase lap
        Assert.Equal(1 + RebaseLaps, runner.GitCalls.Count(c => c.StartsWith("push ", StringComparison.Ordinal)));
        Assert.DoesNotContain(github.Requests, r => r.IsMerge);
        Assert.Equal(merge.Id, (await harness.ParcelAsync(build.ParcelId)).LiveFlowRunId);
    }

    [Fact]
    public async Task A_conflicting_rebase_stopped_rebased_by_hand_and_moved_back_to_build_is_checked_out_pushed_and_merged()
    {
        const string ByHand = "1234123412341234123412341234123412341234";
        var runner = new ScriptedCommandRunner();
        var github = new FakeGitHub();
        var pr = new RemotePullRequest(runner, github);
        pr.BehindWhen = (head, _, _) => head == ScriptedCommandRunner.PrimaryHead;
        runner.ConflictOnRebase("src/Program.cs");
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Scripts.Answer("done", 1), Scripts.Answer("done", 4)],
            ["reviewer-adversarial"] = [Scripts.Answer("clean", 2), Scripts.Answer("clean", 5)],
            ["reviewer-conformance"] = [Scripts.Answer("clean", 3), Scripts.Answer("clean", 6)],
        });
        await using Harness harness = await Harness.StartAsync(github, project: null, executor: executor, runner: runner);

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("Conflicts with main", "paused", pr);
        Assert.Equal("rebase", merge.CurrentStep);
        Assert.Contains("stopped on a conflict", merge.PauseReason, StringComparison.Ordinal);
        Assert.Contains("src/Program.cs", merge.PauseReason, StringComparison.Ordinal);
        Assert.Contains("move the task back to build", merge.Lever, StringComparison.Ordinal);

        await FlowRunDriver.StopAsync(harness.Client, Project, merge.Id, "resolving the conflict by hand");
        await FlowRunDriver.UntilAsync(harness.Client, Project, merge.Id, "stopped");
        string clone = harness.Home.ProjectClonePath(Project);
        await FlowRunDriver.UntilAsync(() => !runner.Worktrees(clone).Any(), "the stopped merge flow run's worktree is removed");
        runner.ConflictOnRebase();
        runner.AddBranch(clone, pr.Branch, ByHand);
        // the remote still holds what the earlier chain's open-pr pushed
        Assert.Equal(ScriptedCommandRunner.PrimaryHead, runner.RemoteBranch(clone, pr.Branch));

        await harness.Client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, build.ParcelId, "build"), None);
        FlowRunView rebuild = await FlowRunDriver.UntilAsync(
            harness.Client, Project,
            (await WaitForAsync(harness, build.ParcelId, f => f.FlowName == "build" && f.Id != build.Id)).Id,
            "ended");

        // a move back to build starts a fresh chain
        Assert.Equal(rebuild.Id, rebuild.RootFlowRunId);
        Assert.Equal(ByHand, rebuild.Steps[0].Result.GetValueOrDefault().GetProperty("head").GetString());
        Assert.Contains($"push --force-with-lease=refs/heads/{pr.Branch}:{ScriptedCommandRunner.PrimaryHead} origin {ByHand}:refs/heads/{pr.Branch}", runner.GitCalls);
        FlowRunView merged = await FlowRunDriver.MergedAsync(harness.Client, Project, build.ParcelId, rebuild.Id);
        Assert.Equal(["gated-merge"], merged.Steps.Select(s => s.Step));
        Assert.Equal(ByHand, (string?)JsonNode.Parse(github.Requests.Single(r => r.IsMerge).Body ?? "{}")?["sha"]);
    }

    [Fact]
    public async Task A_close_its_gate_refuses_pauses_the_merge_holding_the_open_task_and_the_resume_closes_it_without_a_second_merge_request()
    {
        var github = new FakeGitHub();
        var hold = new HoldingGate();
        await using Harness harness = await Harness.StartAsync(github, project: null, hold);
        await harness.PublishTypeAsync(SoftwareTypes.UnownedTask with
        {
            GateBindings = [.. SoftwareTypes.Task.GateBindings, new GateBinding("review", SoftwareTypes.Closed, HoldingGate.GateName)],
        });

        (FlowRunView build, FlowRunView merge) = await harness.BuildThenMergeAsync("Closed only when released", "paused");

        Assert.StartsWith("Flow run could not go on from step 'gated-merge': ", merge.PauseReason, StringComparison.Ordinal);
        Assert.Contains($"of flow 'merge' reached its end, but its parcel could not be closed as 'completed'", merge.PauseReason, StringComparison.Ordinal);
        Assert.Contains($"The gate '{HoldingGate.GateName}' refused the move from 'review' to 'closed': {HoldingGate.Reason}", merge.PauseReason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("gated-merge"), merge.Lever);
        ParcelView held = await harness.ParcelAsync(build.ParcelId);
        Assert.Equal("review", held.Stage);
        Assert.Null(held.Resolution);
        // the end, the release, and the close were recorded together or not at all
        Assert.Equal(merge.Id, held.LiveFlowRunId);
        Assert.Single(github.Requests, r => r.IsMerge);

        hold.Holding = false;
        await FlowRunDriver.ResumeAsync(harness.Client, Project, merge.Id, "the gate no longer holds");
        FlowRunView ended = await FlowRunDriver.UntilAsync(harness.Client, Project, merge.Id, "ended");

        Assert.Equal("merged", ended.Outcome);
        // the resume performs gated-merge again as a new visit
        Assert.Equal(2, ended.Steps.Where(s => s.Step == "gated-merge").Count());
        // the pull request is already merged, so the resume makes no second merge request
        Assert.Single(github.Requests, r => r.IsMerge);
        await harness.ExpectClosedAsync(build.ParcelId, merge.Id);
    }

    [Fact]
    public async Task In_a_project_whose_qa_flow_follows_the_build_the_merge_follows_qa_and_merges_only_after_qa_ended()
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
        await Publishing.FlowAsync(harness.Daemon, Project, Qa);
        await Publishing.FlowAsync(harness.Daemon, Project, MergeAfterQa);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(harness.Client, Project, "QA first");
        FlowRunView build = await FlowRunDriver.BoundAsync(harness.Client, Project, parcel.Id);
        await FlowRunDriver.UntilAsync(harness.Client, Project, build.Id, "ended");

        // QA holds its review open: the build's end started QA alone, and nothing has asked GitHub to merge.
        await executor.Holding.WaitAsync(TimeSpan.FromSeconds(15));
        FlowRunView qa = await FlowRunDriver.FollowerAsync(harness.Client, Project, parcel.Id, build.Id, Qa.Name);
        Assert.Equal(["build", "qa"], (await FlowRunDriver.OfParcelAsync(harness.Client, Project, parcel.Id)).Select(f => f.FlowName));
        Assert.DoesNotContain(github.Requests, r => r.IsMerge || r.IsPullRequestRead);

        executor.Open();
        qa = await FlowRunDriver.UntilAsync(harness.Client, Project, qa.Id, "ended");
        FlowRunView merge = await FlowRunDriver.FollowerAsync(harness.Client, Project, parcel.Id, qa.Id, "merge");
        merge = await FlowRunDriver.UntilAsync(harness.Client, Project, merge.Id, "ended");

        // the project's own merge replaces the shipped one by name
        Assert.Equal(MergeAfterQa.Hash, merge.FlowHash);
        Assert.Equal(2, merge.Depth);
        Assert.Equal(build.Id, merge.RootFlowRunId);
        Assert.Equal(["build", "qa", "merge"], (await FlowRunDriver.OfParcelAsync(harness.Client, Project, parcel.Id)).Select(f => f.FlowName));
        Assert.Single(github.Requests, r => r.IsMerge);
        Assert.NotNull(mergedAt);
        Assert.True(mergedAt >= qa.UpdatedAt, $"Expected the merge request at or after {qa.UpdatedAt:O}, but it was made at {mergedAt:O}: the merge request was made after the QA flow run ended.");
        await harness.ExpectClosedAsync(parcel.Id, merge.Id);
    }

    [Fact]
    public async Task A_qa_flow_run_whose_table_pauses_it_on_a_failing_answer_starts_no_merge_and_leaves_the_task_open_in_review()
    {
        var github = new FakeGitHub();
        await using Harness harness = await Harness.StartAsync(github, project: null, executor: QaScript("not-clean"));
        await Publishing.FlowAsync(harness.Daemon, Project, Qa);
        await Publishing.FlowAsync(harness.Daemon, Project, MergeAfterQa);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(harness.Client, Project, "QA fails");
        FlowRunView build = await FlowRunDriver.BoundAsync(harness.Client, Project, parcel.Id);
        await FlowRunDriver.UntilAsync(harness.Client, Project, build.Id, "ended");
        FlowRunView qa = await FlowRunDriver.FollowerAsync(harness.Client, Project, parcel.Id, build.Id, Qa.Name);

        qa = await FlowRunDriver.UntilAsync(harness.Client, Project, qa.Id, "paused");

        Assert.Contains("pauses on its answer", qa.PauseReason, StringComparison.Ordinal);
        await harness.ExpectNoMergeAsync(parcel.Id, ["build", "qa"]);
    }

    [Fact]
    public async Task A_qa_flow_run_a_person_stops_starts_no_merge_and_leaves_the_task_open_in_review()
    {
        var github = new FakeGitHub();
        var executor = new GatedExecutor(QaScript("clean")) { FreeCollects = 3 };
        await using Harness harness = await Harness.StartAsync(github, project: null, executor: executor);
        await Publishing.FlowAsync(harness.Daemon, Project, Qa);
        await Publishing.FlowAsync(harness.Daemon, Project, MergeAfterQa);
        ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(harness.Client, Project, "QA stopped");
        FlowRunView build = await FlowRunDriver.BoundAsync(harness.Client, Project, parcel.Id);
        await FlowRunDriver.UntilAsync(harness.Client, Project, build.Id, "ended");
        await executor.Holding.WaitAsync(TimeSpan.FromSeconds(15));
        FlowRunView qa = await FlowRunDriver.FollowerAsync(harness.Client, Project, parcel.Id, build.Id, Qa.Name);

        await FlowRunDriver.StopAsync(harness.Client, Project, qa.Id, "QA is not wanted on this one");

        Assert.Equal("stopped", (await FlowRunDriver.UntilAsync(harness.Client, Project, qa.Id, "stopped")).Status);
        await harness.ExpectNoMergeAsync(parcel.Id, ["build", "qa"]);
    }

    /// <summary>Waits for a flow run on the parcel that <paramref name="which"/> picks.</summary>
    private static async Task<FlowRunView> WaitForAsync(Harness harness, Guid parcelId, Func<FlowRunView, bool> which)
    {
        FlowRunView? found = null;
        await FlowRunDriver.UntilAsync(
            async () => (found = (await FlowRunDriver.OfParcelAsync(harness.Client, Project, parcelId)).FirstOrDefault(which)) is not null,
            "the flow run is on the parcel");
        return found ?? throw new InvalidOperationException("The wait ended with no flow run.");
    }

    /// <summary>
    /// The merge flow run's worktree is removed at most once, by its own end after its rebase pushed,
    /// never by the build flow run's removal racing its takeover, and the build flow run recorded nothing
    /// after it ended.
    /// </summary>
    private static async Task ExpectOnlyTheMergesOwnEndRemovedItsWorktreeAsync(ScriptedCommandRunner runner, Harness harness, FlowRunView build, FlowRunView merge)
    {
        string mergePath = harness.Home.FlowRunWorkPath(Project, merge.Id);
        IReadOnlyList<string> calls = runner.GitCalls;
        int lastPush = calls.Select((c, i) => (c, i)).Last(p => p.c.StartsWith("push ", StringComparison.Ordinal)).i;
        int[] removals = [.. calls.Select((c, i) => (c, i)).Where(p => p.c == $"worktree remove --force --force {mergePath}").Select(p => p.i)];
        Assert.True(removals.Length <= 1, $"Expected at most one removal of the merge's worktree, but found {removals.Length}.");
        Assert.All(removals, i => Assert.True(i > lastPush));
        FlowRunView after = await FlowRunDriver.ShowAsync(harness.Client, Project, build.Id);
        Assert.Equal(build.Steps.Count, after.Steps.Count);
        Assert.Null(after.PauseReason);
    }

    /// <summary>
    /// GitHub's view of the task's pull request, read from the scripted runner's remote: its head is
    /// whatever the remote's branch holds, it reads merged once a merge is accepted, each comparison
    /// with the primary branch asks <see cref="BehindWhen"/> with the head and how many comparisons
    /// have been made on this visit's head, and the required check on a head reports
    /// <see cref="ChecksOn"/>.
    /// </summary>
    private sealed class RemotePullRequest
    {
        private readonly ScriptedCommandRunner runner;
        private string clone = string.Empty;
        private int inspections;
        private int compares;
        private volatile bool merged;

        public RemotePullRequest(ScriptedCommandRunner runner, FakeGitHub github)
        {
            this.runner = runner;
            github.On(r => r.IsPullRequestRead, _ =>
            {
                Interlocked.Increment(ref inspections);
                Interlocked.Exchange(ref compares, 0);
                return FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, merged: merged, head: Head));
            });
            github.On(r => r.IsCompare, _ => FakeGitHub.Json(HttpStatusCode.OK, new JsonObject
            {
                ["ahead_by"] = 1,
                ["behind_by"] = BehindWhen(Head, Volatile.Read(ref inspections), Interlocked.Increment(ref compares)) ? 1 : 0,
            }));
            github.On(r => r.IsCheckRuns, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.CheckRuns(
                [ChecksOn(Head) switch
                {
                    "success" => FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "completed", "success"),
                    string running => FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, running, null),
                }],
                total: 1)));
            github.On(r => r.IsMerge, _ =>
            {
                merged = true;
                return FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["sha"] = FakeGitHub.MergeCommit, ["merged"] = true });
            });
        }

        public string Branch { get; private set; } = string.Empty;

        /// <summary>Whether a comparison finds the head behind, given the head, the inspection's number, and the comparison's number within it.</summary>
        public Func<string, int, int, bool> BehindWhen { get; set; } = (_, _, _) => false;

        /// <summary>The required check's status on a head: success, or a status still running.</summary>
        public Func<string, string> ChecksOn { get; set; } = _ => "success";

        private string Head => runner.RemoteBranch(clone, Branch) ?? ScriptedCommandRunner.PrimaryHead;

        public void Track(string clonePath, Guid parcelId)
        {
            clone = clonePath;
            Branch = $"coanda/{Coanda.Core.Ids.Short(parcelId)}";
        }
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

        public GateVerdict Check(Parcel parcel, ParcelType type, string fromStage, string toStage) =>
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
            Home = temp.Home;
            this.temp = temp;
            Daemon = daemon;
            GitHub = github;
            Client = new DaemonClient(temp.Home);
        }

        public RunningDaemon Daemon { get; }

        public NodeHome Home { get; }

        public FakeGitHub GitHub { get; }

        public DaemonClient Client { get; }

        public static async Task<Harness> StartAsync(
            FakeGitHub github, Flow? project, IGate? gate = null, IAgentExecutor? executor = null, ScriptedCommandRunner? runner = null)
        {
            var temp = new TemporaryHome();
            RunningDaemon daemon = await Daemons.StartAsync(
                temp.Home,
                new DaemonOptions
                {
                    Executor = executor ?? Scripts.CleanBuildExecutor(),
                    GitHubHandler = github.Handler,
                    Gates = gate is null ? [] : [gate],
                    CommandRunner = runner,
                },
                None);
            await RegisteredProjects.AddAsync(daemon, Project, None);
            if (project is not null)
            {
                await Publishing.FlowAsync(daemon, Project, project);
            }

            return new Harness(temp, daemon, github);
        }

        public async Task PublishTypeAsync(ParcelType type)
        {
            DefinitionService definitions = Daemon.Services.GetRequiredService<DefinitionService>();
            Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, Project, type, null, None);
            await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        }

        /// <summary>
        /// Runs a task's build to its end and waits for the merge that follows to reach
        /// <paramref name="status"/>; <paramref name="pullRequest"/>, when given, is told the task's branch.
        /// </summary>
        public async Task<(FlowRunView Build, FlowRunView Merge)> BuildThenMergeAsync(string title, string status, RemotePullRequest? pullRequest = null)
        {
            ParcelView parcel = await FlowRunDriver.TaskAtBuildAsync(Client, Project, title);
            pullRequest?.Track(Home.ProjectClonePath(Project), parcel.Id);
            FlowRunView build = await FlowRunDriver.BoundAsync(Client, Project, parcel.Id);
            build = await FlowRunDriver.UntilAsync(Client, Project, build.Id, "ended");
            FlowRunView merge = await FlowRunDriver.FollowerAsync(Client, Project, parcel.Id, build.Id, "merge");
            return (build, await FlowRunDriver.UntilAsync(Client, Project, merge.Id, status));
        }

        public Task<ParcelView> ParcelAsync(Guid parcelId) =>
            Client.GetAsync<ParcelView>($"/parcels/{parcelId}?project={Project}", None);

        /// <summary>The task closed as completed, released, by the merge flow run's own principal, with a reason naming the flow run and its flow.</summary>
        public async Task ExpectClosedAsync(Guid parcelId, Guid mergeId)
        {
            ParcelView parcel = await ParcelAsync(parcelId);
            Assert.Equal("closed", parcel.Stage);
            Assert.Equal("completed", parcel.Resolution);
            Assert.Null(parcel.LiveFlowRunId);
            Assert.Contains($"of flow 'merge' reached its end", parcel.CloseReason, StringComparison.Ordinal);

            await using IQuerySession session = Daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Project);
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: None);
            ParcelClosed closed = Assert.Single(events.Select(e => e.Data).OfType<ParcelClosed>());
            Assert.Equal(FlowRunAdvancer.PrincipalFor(mergeId).Id, closed.ClosedBy);
            object[] tail = [.. events.TakeLast(2).Select(e => e.Data)];
            Assert.Equal(mergeId, Assert.IsType<ParcelFlowRunReleased>(tail[0]).FlowRunId);
            Assert.Same(closed, tail[1]);
            long[] versions = [.. events.TakeLast(2).Select(e => e.Version)];
            Assert.Equal(versions.Order(), versions);
        }

        /// <summary>
        /// No merge flow run started and no merge was asked for, and the task is open in review. A later
        /// task's bound build proves the trigger loop has read past everything this one recorded.
        /// </summary>
        public async Task ExpectNoMergeAsync(Guid parcelId, string[] flowRuns)
        {
            ParcelView later = await FlowRunDriver.TaskAtBuildAsync(Client, Project, "A later task");
            await FlowRunDriver.BoundAsync(Client, Project, later.Id);

            Assert.Equal(flowRuns, (await FlowRunDriver.OfParcelAsync(Client, Project, parcelId)).Select(f => f.FlowName));
            Assert.DoesNotContain(GitHub.Requests, r => r.IsMerge);
            ParcelView parcel = await ParcelAsync(parcelId);
            Assert.Equal("review", parcel.Stage);
            Assert.Null(parcel.Resolution);
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
