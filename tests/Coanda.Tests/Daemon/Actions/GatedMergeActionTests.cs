using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.GitHub;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.GitHub;
using Fisher;

namespace Coanda.Tests.Daemon.Actions;

/// <summary>
/// The gated-merge action against a fake GitHub: what it inspects, when it merges, waits, answers
/// behind, or pauses, and that it merges exactly the head the chain's latest open-pr or rebase result
/// recorded. The flow run it runs on is a contour of open-pr, rebase, and worktree steps whose results the
/// test records by hand, so the chain is that flow run.
/// </summary>
public sealed class GatedMergeActionTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private const string Head = ScriptedCommandRunner.PrimaryHead;
    private const string Other = "aaaabbbbccccddddeeeeffff0000111122223333";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal FlowRunPrincipal = new("flow-run:under-test", Role.Agent);
    private static readonly ContourStep Step = new()
    {
        Name = "gated-merge",
        Action = SoftwareActions.GatedMerge,
        Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of(GatedMergeAction.MergedVerdict), VisitCap = 1, Target = Contour.End }],
    };

    /// <summary>A contour of open-pr, rebase, and worktree steps, so a flow run of it can hold a result of each.</summary>
    private static readonly Contour Recorder = new()
    {
        Name = "recorder",
        Accepts = ["task"],
        Outcome = "recorded",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            new ContourStep
            {
                Name = "open-pr",
                Action = SoftwareActions.OpenPr,
                Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("opened"), VisitCap = 1, Target = "rebase" }],
            },
            new ContourStep
            {
                Name = "rebase",
                Action = SoftwareActions.Rebase,
                Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("done"), VisitCap = 1, Target = "worktree" }],
            },
            new ContourStep
            {
                Name = "worktree",
                Action = SoftwareActions.Worktree,
                Transitions = [new ContourTransition { Field = "verdict", Value = ContourValue.Of("done"), VisitCap = 1, Target = Contour.End }],
            },
        ],
    };

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private readonly FakeGitHub github = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private ProjectService projects = null!;
    private FlowRunService flowRuns = null!;
    private GatedMergeAction action = null!;

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        parcels = new ParcelService(store, TimeProvider.System);
        projects = new ProjectService(store, TimeProvider.System);
        flowRuns = new FlowRunService(store, TimeProvider.System);
        var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Contour, DefinitionScope.Project, Project, Recorder.Name,
            Contour.CanonicalJson(Recorder), Recorder.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        action = new GatedMergeAction(parcels, projects, flowRuns, new GitHubToken(runner, temp.Home), github.Client);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task A_clean_pull_request_is_merged_at_the_recorded_head_by_the_preferred_method_after_one_inspection_of_each_kind()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();

        JsonElement result = Merged(await PerformAsync(parcel, flowRun));

        result.GetProperty("commit").GetString().Should().Be(FakeGitHub.MergeCommit);
        result.GetProperty("action").GetString().Should().Be(SoftwareActions.GatedMerge);
        github.Requests.Select(r => $"{r.Method} {r.Uri.AbsolutePath}").Should().Equal(
            "GET /repos/example/repo/pulls/17",
            $"GET /repos/example/repo/compare/main...{Head}",
            $"GET /repos/example/repo/commits/{Head}/check-runs",
            $"GET /repos/example/repo/commits/{Head}/statuses",
            "GET /repos/example/repo",
            $"GET /repos/example/repo/compare/main...{Head}",
            "PUT /repos/example/repo/pulls/17/merge");
        github.Requests.Should().OnlyContain(r => r.Authorization == $"Bearer {ScriptedCommandRunner.GitHubToken}");
        JsonNode body = JsonNode.Parse(MergeRequest().Body ?? "{}") ?? new JsonObject();
        ((string?)body["sha"]).Should().Be(Head, "the merge carries the inspected head as the expected head");
        ((string?)body["merge_method"]).Should().Be("merge");
        body.AsObject().Select(p => p.Key).Should().BeEquivalentTo(["sha", "merge_method"], "nothing asks GitHub to merge later on its own");
        github.Requests.Should().NotContain(r => r.Uri.AbsolutePath.Contains("auto", StringComparison.OrdinalIgnoreCase) || r.Uri.AbsolutePath.Contains("graphql", StringComparison.Ordinal));
        runner.Commands.Should().OnlyContain(c => c.Program == "gh", "the merge reads only the token, nothing from the node's clone");
        github.Unexpected.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, true, true, "merge")]
    [InlineData(false, true, true, "squash")]
    [InlineData(false, false, true, "rebase")]
    [InlineData(true, false, true, "merge")]
    public async Task The_merge_method_is_read_from_the_repository_preferring_merge_then_squash_then_rebase(bool merge, bool squash, bool rebase, string expected)
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsRepositoryRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.MergeMethods(merge, squash, rebase)));

        Merged(await PerformAsync(parcel, flowRun));

        ((string?)JsonNode.Parse(MergeRequest().Body ?? "{}")?["merge_method"]).Should().Be(expected);
    }

    [Fact]
    public async Task An_already_merged_pull_request_answers_merged_with_its_commit_and_checks_nothing_else()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, merged: true, baseRef: "elsewhere", head: Other)));
        github.On(r => r.IsCompare, _ => FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["behind_by"] = 4 }));

        JsonElement result = Merged(await PerformAsync(parcel, flowRun));

        result.GetProperty("commit").GetString().Should().Be(FakeGitHub.MergeCommit);
        github.Requests.Should().ContainSingle("a merged pull request is answered before any guard or check is read").Which.IsPullRequestRead.Should().BeTrue();
    }

    [Fact]
    public async Task An_already_merged_pull_request_answers_merged_even_with_no_open_pr_result_and_no_named_check()
    {
        Guid parcel = await TaskAsync();
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel, Recorder.Name, None);
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, merged: true)));

        Merged(await PerformAsync(parcel, started.Id));
    }

    [Fact]
    public async Task A_check_run_still_in_progress_answers_pending_with_a_reason_and_merges_nothing()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre(FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "in_progress", null));

        string reason = Pending(await PerformAsync(parcel, flowRun));

        reason.Should().Contain($"the required check '{RegisteredProjects.RequiredCheck}' is in_progress");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task A_named_check_still_pending_after_another_named_check_succeeded_answers_pending_naming_it()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        await projects.SetRequiredChecksAsync(Principal.LocalOwner, Project, ["build-test (ubuntu-latest)", "build-test (windows-latest)"], None);
        RunsAre(
            FakeGitHub.CheckRun(1, "build-test (ubuntu-latest)", "completed", "success"),
            FakeGitHub.CheckRun(2, "build-test (windows-latest)", "queued", null));

        string reason = Pending(await PerformAsync(parcel, flowRun));

        reason.Should().Contain("'build-test (windows-latest)' is queued").And.NotContain("ubuntu");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task A_named_check_not_yet_reported_answers_pending()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre();

        Pending(await PerformAsync(parcel, flowRun)).Should().Contain($"the required check '{RegisteredProjects.RequiredCheck}' has not been reported");
    }

    [Fact]
    public async Task A_named_check_reported_as_a_pending_commit_status_answers_pending_and_a_successful_status_merges()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre();
        StatusesAre(FakeGitHub.Status(1, RegisteredProjects.RequiredCheck, "pending"));

        Pending(await PerformAsync(parcel, flowRun)).Should().Contain("is pending");

        StatusesAre(FakeGitHub.Status(1, RegisteredProjects.RequiredCheck, "pending"), FakeGitHub.Status(2, RegisteredProjects.RequiredCheck, "success"));
        Merged(await PerformAsync(parcel, flowRun));
    }

    [Fact]
    public async Task Another_check_still_running_or_a_status_pending_answers_pending()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre(FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "completed", "success"), FakeGitHub.CheckRun(2, "lint", "in_progress", null));
        StatusesAre(FakeGitHub.Status(3, "ci/legacy", "pending"));

        Pending(await PerformAsync(parcel, flowRun)).Should().Contain("the check 'lint' is in_progress").And.Contain("the check 'ci/legacy' is pending");
    }

    [Fact]
    public async Task Mergeability_not_yet_computed_answers_pending()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, mergeable: null)));

        Pending(await PerformAsync(parcel, flowRun)).Should().Contain("has not yet computed whether pull request #17");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task Other_checks_that_completed_neutral_or_skipped_and_a_successful_status_do_not_hold_the_merge()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre(
            FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "completed", "success"),
            FakeGitHub.CheckRun(2, "optional", "completed", "neutral"),
            FakeGitHub.CheckRun(3, "skipped-one", "completed", "skipped"));
        StatusesAre(FakeGitHub.Status(4, "ci/legacy", "success"));

        Merged(await PerformAsync(parcel, flowRun));
    }

    [Fact]
    public async Task A_check_counts_by_its_latest_run_and_a_status_by_its_latest_context_so_a_failure_rerun_to_success_merges()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre(
            FakeGitHub.CheckRun(7, RegisteredProjects.RequiredCheck, "completed", "success"),
            FakeGitHub.CheckRun(3, RegisteredProjects.RequiredCheck, "completed", "failure"));
        StatusesAre(FakeGitHub.Status(9, "ci/legacy", "success"), FakeGitHub.Status(5, "ci/legacy", "failure"));

        Merged(await PerformAsync(parcel, flowRun));
    }

    [Fact]
    public async Task Every_page_of_check_runs_and_statuses_is_read()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        JsonObject[] fullPage = [.. Enumerable.Range(1, 100).Select(n => FakeGitHub.CheckRun(n, $"other-{n}", "completed", "success"))];
        github.On(r => r.IsCheckRuns, r => FakeGitHub.Json(HttpStatusCode.OK, r.Page == 1
            ? FakeGitHub.CheckRuns(fullPage, total: 101)
            : FakeGitHub.CheckRuns([FakeGitHub.CheckRun(500, RegisteredProjects.RequiredCheck, "completed", "success")], total: 101)));
        JsonObject[] statusPage = [.. Enumerable.Range(1, 100).Select(n => FakeGitHub.Status(n, $"status-{n}", "success"))];
        github.On(r => r.IsStatuses, r => FakeGitHub.Json(HttpStatusCode.OK, r.Page switch
        {
            1 => new JsonArray([.. statusPage]),
            2 => new JsonArray(FakeGitHub.Status(900, "late-context", "failure")),
            _ => new JsonArray(),
        }));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel, flowRun));

        paused.Reason.Should().Contain("the check 'late-context' reported the status 'failure'", "the failure is on the second page of statuses");
        github.Requests.Where(r => r.IsCheckRuns).Select(r => r.Page).Should().Equal(1, 2);
        github.Requests.Where(r => r.IsStatuses).Select(r => r.Page).Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_task_whose_pull_request_field_has_no_value_pauses()
    {
        Guid parcel = await TaskAsync(pullRequest: false);
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel, Recorder.Name, None);

        PausedToPerformAgain(await PerformAsync(parcel, started.Id)).Should().Contain("the task's pull-request field has no value");
        github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_chain_with_no_open_pr_result_recording_a_head_pauses()
    {
        Guid parcel = await TaskAsync();
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcel, Recorder.Name, None);

        PausedToPerformAgain(await PerformAsync(parcel, started.Id)).Should().Contain("no open-pr or rebase result with a head is recorded");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task No_github_token_pauses()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        runner.On(c => c.Program == "gh", ScriptedCommandRunner.Fails(1, "no oauth token found for github.com"));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain("no GitHub token is available");
        github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_remote_that_is_no_github_repository_pauses()
    {
        await projects.AddAsync(Principal.LocalOwner, "elsewhere", "git@gitlab.example:team/repo.git", "main", None);
        await projects.SetRequiredChecksAsync(Principal.LocalOwner, "elsewhere", [RegisteredProjects.RequiredCheck], None);
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, "elsewhere", "task", "Merge me", null, None);
        await parcels.SetFieldAsync(Principal.LocalOwner, "elsewhere", parcel.Id, TaskFields.PullRequest, FieldValue.OfReference("17", FakeGitHub.Url), None);

        ActionOutcome outcome = await action.PerformAsync(new ActionRequest(FlowRunPrincipal, "elsewhere", Ids.New(), parcel.Id, Recorder, Step, false), None);

        PausedToPerformAgain(outcome).Should().Contain("is not a github.com repository");
        github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_that_names_no_check_pauses_rather_than_merging()
    {
        await projects.AddAsync(Principal.LocalOwner, "unchecked", RegisteredProjects.Remote, RegisteredProjects.Branch, None);
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, "unchecked", "task", "Merge me", null, None);
        await parcels.SetFieldAsync(Principal.LocalOwner, "unchecked", parcel.Id, TaskFields.PullRequest, FieldValue.OfReference("17", FakeGitHub.Url), None);
        Guid flowRun = await RecordedFlowRunAsync("unchecked", parcel.Id, Head);

        ActionOutcome outcome = await action.PerformAsync(new ActionRequest(FlowRunPrincipal, "unchecked", flowRun, parcel.Id, Recorder, Step, false), None);

        PausedToPerformAgain(outcome).Should().Contain("project 'unchecked' names no check for a merge to wait for");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task A_pull_request_closed_without_being_merged_pauses()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, state: "closed")));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain("pull request #17 in example/repo is closed without being merged");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task A_pull_request_on_another_base_pauses()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, baseRef: "release")));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain("targets 'release', not the primary branch 'main'");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task A_head_that_is_not_the_recorded_commit_pauses()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, head: Other)));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain($"the head of pull request #17 in example/repo is {Other}, not {Head}");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task The_latest_open_pr_result_in_the_chain_names_the_head_held_to()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync(head: Other);
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, head: Other)));

        Merged(await PerformAsync(parcel, flowRun));

        ((string?)JsonNode.Parse(MergeRequest().Body ?? "{}")?["sha"]).Should().Be(Other);
    }

    [Fact]
    public async Task A_head_behind_the_primary_branch_answers_behind_before_mergeability_and_the_checks_are_read()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsCompare, _ => FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["ahead_by"] = 1, ["behind_by"] = 2 }));

        JsonElement result = Behind(await PerformAsync(parcel, flowRun));

        result.GetProperty("reason").GetString().Should().Contain("is behind the primary branch 'main' by 2 commits");
        result.GetProperty("behind").GetInt32().Should().Be(2);
        github.Requests.Should().NotContain(r => r.IsMerge || r.IsCheckRuns || r.IsStatuses || r.IsRepositoryRead);
    }

    [Theory]
    [InlineData("completed", "failure")]
    [InlineData("in_progress", null)]
    public async Task A_behind_head_answers_behind_whatever_its_checks_report(string status, string? conclusion)
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre(FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, status, conclusion));
        github.On(r => r.IsCompare, _ => FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["ahead_by"] = 1, ["behind_by"] = 1 }));

        Behind(await PerformAsync(parcel, flowRun));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task A_behind_head_answers_behind_whatever_github_reports_of_its_mergeability(bool? mergeable)
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, mergeable: mergeable)));
        github.On(r => r.IsCompare, _ => FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["ahead_by"] = 1, ["behind_by"] = 1 }));

        Behind(await PerformAsync(parcel, flowRun));
    }

    [Fact]
    public async Task A_primary_branch_that_moves_while_the_checks_are_read_answers_behind_before_the_merge()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        int compares = 0;
        github.On(r => r.IsCompare, _ =>
        {
            // The first comparison, before the checks, finds the head current; the second finds a commit landed since.
            int behind = Interlocked.Increment(ref compares) == 1
                ? 0
                : 1;
            return FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["ahead_by"] = 1, ["behind_by"] = behind });
        });

        JsonElement result = Behind(await PerformAsync(parcel, flowRun));

        result.GetProperty("reason").GetString().Should().Contain("is behind the primary branch 'main' by 1 commit,");
        github.Requests.Where(r => r.IsCompare).Should().HaveCount(2, "the primary branch is compared again after the checks and methods are read");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task A_project_that_names_no_check_pauses_before_a_behind_head_is_answered_behind()
    {
        await projects.AddAsync(Principal.LocalOwner, "unchecked", RegisteredProjects.Remote, RegisteredProjects.Branch, None);
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, "unchecked", "task", "Merge me", null, None);
        await parcels.SetFieldAsync(Principal.LocalOwner, "unchecked", parcel.Id, TaskFields.PullRequest, FieldValue.OfReference("17", FakeGitHub.Url), None);
        Guid flowRun = await RecordedFlowRunAsync("unchecked", parcel.Id, Head);
        github.On(r => r.IsCompare, _ => FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["ahead_by"] = 1, ["behind_by"] = 3 }));

        ActionOutcome outcome = await action.PerformAsync(new ActionRequest(FlowRunPrincipal, "unchecked", flowRun, parcel.Id, Recorder, Step, false), None);

        PausedToPerformAgain(outcome).Should().Contain("names no check");
        github.Requests.Should().NotContain(r => r.IsCompare);
    }

    [Theory]
    [InlineData("closed", RegisteredProjects.Branch, Head)]
    [InlineData("open", "elsewhere", Head)]
    [InlineData("open", RegisteredProjects.Branch, Other)]
    public async Task A_closed_pull_request_another_base_or_another_head_pauses_before_a_behind_head_is_answered_behind(string state, string baseRef, string head)
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, state: state, baseRef: baseRef, head: head)));
        github.On(r => r.IsCompare, _ => FakeGitHub.Json(HttpStatusCode.OK, new JsonObject { ["ahead_by"] = 1, ["behind_by"] = 3 }));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun));
        github.Requests.Should().NotContain(r => r.IsCompare);
    }

    [Fact]
    public async Task A_rebase_result_recorded_after_the_open_pr_result_names_the_head_held_to_and_a_worktree_result_never_does()
    {
        const string Rebased = "1111222233334444555566667777888899990000";
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        await flowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRun, "rebase", None);
        await flowRuns.RecordResultAsync(
            Principal.LocalOwner, Project, flowRun, "rebase", SoftwareActions.Rebase,
            JsonSerializer.SerializeToElement(new { verdict = "done", action = SoftwareActions.Rebase, head = Rebased }), None);
        await flowRuns.StartStepAsync(Principal.LocalOwner, Project, flowRun, "worktree", None);
        await flowRuns.RecordResultAsync(
            Principal.LocalOwner, Project, flowRun, "worktree", SoftwareActions.Worktree,
            JsonSerializer.SerializeToElement(new { verdict = "done", action = SoftwareActions.Worktree, head = Other }), None);
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, head: Rebased)));

        Merged(await PerformAsync(parcel, flowRun));

        ((string?)JsonNode.Parse(MergeRequest().Body ?? "{}")?["sha"]).Should().Be(Rebased);
    }

    [Fact]
    public async Task A_conflict_pauses()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsPullRequestRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.PullRequestState(17, mergeable: false)));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain("has a conflict with 'main'");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("cancelled")]
    [InlineData("neutral")]
    [InlineData("timed_out")]
    public async Task A_named_check_that_completed_other_than_success_pauses_naming_it(string conclusion)
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre(FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "completed", conclusion));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun))
            .Should().Contain($"the required check '{RegisteredProjects.RequiredCheck}' completed with the conclusion '{conclusion}'");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("error")]
    public async Task A_named_check_whose_latest_status_failed_pauses_naming_it(string state)
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre();
        StatusesAre(FakeGitHub.Status(1, RegisteredProjects.RequiredCheck, state));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun))
            .Should().Contain($"the required check '{RegisteredProjects.RequiredCheck}' reported the status '{state}'");
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timed_out")]
    [InlineData("action_required")]
    [InlineData("stale")]
    public async Task Another_check_that_completed_other_than_success_neutral_or_skipped_pauses_naming_it(string conclusion)
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        RunsAre(FakeGitHub.CheckRun(1, RegisteredProjects.RequiredCheck, "completed", "success"), FakeGitHub.CheckRun(2, "lint", "completed", conclusion));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain($"the check 'lint' completed with the conclusion '{conclusion}'");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task Another_status_that_errored_pauses_naming_it()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        StatusesAre(FakeGitHub.Status(1, "ci/legacy", "error"));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain("the check 'ci/legacy' reported the status 'error'");
    }

    [Fact]
    public async Task A_repository_that_allows_no_merge_method_pauses()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.On(r => r.IsRepositoryRead, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.MergeMethods(false, false, false)));

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain("allows none of the merge methods");
        github.Requests.Should().NotContain(r => r.IsMerge);
    }

    [Fact]
    public async Task A_merge_github_refuses_because_the_head_moved_pauses_saying_so()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.Refuse(r => r.IsMerge, HttpStatusCode.Conflict, "Head branch was modified. Review and try the merge again.");

        PausedToPerformAgain(await PerformAsync(parcel, flowRun))
            .Should().Contain("the head of pull request #17 in example/repo moved after it was inspected").And.Contain("Head branch was modified");
    }

    [Fact]
    public async Task Any_other_request_github_refuses_pauses_quoting_its_status_and_message()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.Refuse(r => r.IsMerge, HttpStatusCode.MethodNotAllowed, "Pull Request is not mergeable");

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain("status 405").And.Contain("Pull Request is not mergeable");
    }

    [Fact]
    public async Task A_refused_inspection_pauses_quoting_its_status_and_message()
    {
        (Guid parcel, Guid flowRun) = await ArrangeAsync();
        github.Refuse(r => r.IsPullRequestRead, HttpStatusCode.NotFound, "Not Found");

        PausedToPerformAgain(await PerformAsync(parcel, flowRun)).Should().Contain("status 404").And.Contain("Not Found");
    }

    private async Task<(Guid Parcel, Guid FlowRun)> ArrangeAsync(string head = Head)
    {
        Guid parcel = await TaskAsync();
        return (parcel, await RecordedFlowRunAsync(Project, parcel, head));
    }

    private async Task<Guid> TaskAsync(bool pullRequest = true)
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "Merge me", null, None);
        if (pullRequest)
        {
            await parcels.SetFieldAsync(Principal.LocalOwner, Project, parcel.Id, TaskFields.PullRequest, FieldValue.OfReference("17", FakeGitHub.Url), None);
        }

        return parcel.Id;
    }

    /// <summary>Starts a flow run of the recorder contour on the parcel and records an open-pr result whose head is <paramref name="head"/>.</summary>
    private async Task<Guid> RecordedFlowRunAsync(string project, Guid parcel, string head)
    {
        if (project != Project)
        {
            var definitions = new DefinitionService(store, TimeProvider.System, actions: ShippedActions.Names);
            Definition draft = await definitions.DraftAsync(
                Principal.LocalOwner, DefinitionKind.Contour, DefinitionScope.Project, project, Recorder.Name,
                Contour.CanonicalJson(Recorder), Recorder.Hash, null, None);
            await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, None);
        }

        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, project, parcel, Recorder.Name, None);
        JsonElement result = JsonSerializer.SerializeToElement(new { verdict = "opened", action = SoftwareActions.OpenPr, number = 17, url = FakeGitHub.Url, head });
        await flowRuns.RecordResultAsync(Principal.LocalOwner, project, started.Id, "open-pr", SoftwareActions.OpenPr, result, None);
        return started.Id;
    }

    private ValueTask<ActionOutcome> PerformAsync(Guid parcel, Guid flowRun) =>
        action.PerformAsync(new ActionRequest(FlowRunPrincipal, Project, flowRun, parcel, Recorder, Step, false), None);

    private void RunsAre(params JsonObject[] runs) =>
        github.On(r => r.IsCheckRuns, _ => FakeGitHub.Json(HttpStatusCode.OK, FakeGitHub.CheckRuns(runs, runs.Length)));

    private void StatusesAre(params JsonObject[] statuses) =>
        github.On(r => r.IsStatuses, _ => FakeGitHub.Json(HttpStatusCode.OK, new JsonArray([.. statuses])));

    private FakeGitHub.Sent MergeRequest() => github.Requests.Should().ContainSingle(r => r.IsMerge).Subject;

    private static JsonElement Merged(ActionOutcome outcome)
    {
        JsonElement result = outcome.Should().BeOfType<ActionOutcome.Done>().Subject.Result;
        result.GetProperty("verdict").GetString().Should().Be(GatedMergeAction.MergedVerdict);
        return result;
    }

    private static string Pending(ActionOutcome outcome)
    {
        JsonElement result = outcome.Should().BeOfType<ActionOutcome.Done>().Subject.Result;
        result.GetProperty("verdict").GetString().Should().Be(GatedMergeAction.PendingVerdict);
        return result.GetProperty("reason").GetString() ?? string.Empty;
    }

    private static JsonElement Behind(ActionOutcome outcome)
    {
        JsonElement result = outcome.Should().BeOfType<ActionOutcome.Done>().Subject.Result;
        result.GetProperty("verdict").GetString().Should().Be(GatedMergeAction.BehindVerdict);
        result.GetProperty("action").GetString().Should().Be(SoftwareActions.GatedMerge);
        return result;
    }

    private static ActionOutcome.Paused Paused(ActionOutcome outcome) => outcome.Should().BeOfType<ActionOutcome.Paused>().Subject;

    private static string PausedToPerformAgain(ActionOutcome outcome)
    {
        ActionOutcome.Paused paused = Paused(outcome);
        paused.Reason.Should().StartWith("Step 'gated-merge' ");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("gated-merge"));
        return paused.Reason;
    }
}
