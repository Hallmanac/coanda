using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Modules.Software.GitHub;
using Coanda.Modules.Software.Repositories;
using Coanda.Modules.Software.Steps;
using Coanda.Sdk.Commands;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Modules.Software.Steps;

/// <summary>
/// The open-pr step against the scripted command runner and a fake GitHub. Each flow run it is
/// dispatched for sits in an open-pr step holding the dispatch, since the step records the pull
/// request on the task under that dispatch.
/// </summary>
public sealed class OpenPrStepTests : IAsyncLifetime
{
    private const string Project = SoftwareNode.Project;
    private const string Moved = "fedcba9876543210fedcba9876543210fedcba98";
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly ScriptedCommandRunner runner = new();
    private readonly FakeGitHub github = new();
    private readonly Dictionary<Guid, Guid> held = [];
    private SoftwareNode node = null!;
    private ParcelService parcels = null!;
    private ProjectService projects = null!;

    private string Clone => node.Home.ProjectClonePath(Project);

    public async Task InitializeAsync() => await StartAsync();

    public async Task DisposeAsync() => await node.DisposeAsync();

    [Fact]
    public async Task A_first_run_pushes_the_branch_expecting_it_absent_opens_a_pull_request_against_the_primary_and_records_it_on_the_task()
    {
        Parcel parcel = await TaskAsync(Project, "Open the pull request");
        string branch = ProjectClones.BranchOf(parcel.Id);
        Guid flowRunId = await HeldAsync(parcel.Id);
        runner.AddBranch(Clone, branch, Moved);

        JsonElement result = Opened(await DispatchAsync(parcel.Id, flowRunId));

        Assert.Equal(FakeGitHub.Number, result.GetProperty("number").GetInt32());
        Assert.Equal(FakeGitHub.Url, result.GetProperty("url").GetString());
        Assert.Equal(Moved, result.GetProperty("head").GetString());
        CommandLine first = runner.Commands[0];
        Assert.True(first.Program == "gh" && string.Join(' ', first.Arguments) == "auth token --hostname github.com");
        Assert.Equal(node.Home.FlowRunWorkPath(Project, flowRunId), runner.Commands.Single(c => c.IsGit("status")).WorkingDirectory);
        Assert.Equal(["status --porcelain --untracked-files=all", $"rev-parse --verify --quiet refs/heads/{branch}^{{commit}}", $"for-each-ref --format=%(objectname) refs/remotes/origin/{branch}", $"push --force-with-lease=refs/heads/{branch}: origin {Moved}:refs/heads/{branch}", $"update-ref refs/remotes/origin/{branch} {Moved}"], runner.GitCalls);
        Assert.All(runner.Commands.Where(c => c.Program == "git" && !c.IsGit("status")), c => Assert.True(c.GitDirectory() == Clone));
        // the next push on the branch expects what this one pushed
        Assert.Equal(Moved, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));

        Assert.Equal([$"GET /repos/example/repo/pulls?state=open&head=example%3Acoanda%2F{Ids.Short(parcel.Id)}", "POST /repos/example/repo/pulls"], github.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"));
        Assert.All(github.Requests, r => Assert.True(r.Uri.Host == "api.github.com" && r.Authorization == $"Bearer {ScriptedCommandRunner.GitHubToken}"));
        JsonNode body = JsonNode.Parse(github.Requests[1].Body ?? "{}") ?? new JsonObject();
        Assert.Equal("Open the pull request", (string?)body["title"]);
        Assert.Equal(branch, (string?)body["head"]);
        Assert.Equal(RegisteredProjects.Branch, (string?)body["base"]);
        Assert.Equal($"{TaskFields.ObjectiveValue.Text}\n\n## Criteria\n\n{string.Concat((TaskFields.CriteriaValue.Items ?? []).Select(c => $"- {c}\n"))}", (string?)body["body"]);

        Assert.Equal(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url), await FieldAsync(parcel.Id));
    }

    [Fact]
    public async Task A_clone_holding_the_branchs_remote_tracking_ref_pushes_with_that_commit_as_the_lease_and_moves_the_ref_to_the_pushed_one()
    {
        Parcel parcel = await TaskAsync(Project, "Push again");
        string branch = ProjectClones.BranchOf(parcel.Id);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), ScriptedCommandRunner.PrimaryHead);

        Opened(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.Contains($"push --force-with-lease=refs/heads/{branch}:{ScriptedCommandRunner.PrimaryHead} origin {Moved}:refs/heads/{branch}", runner.GitCalls);
        Assert.Equal(Moved, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
    }

    [Fact]
    public async Task A_new_flow_run_after_someone_updated_the_branch_on_the_remote_builds_on_their_commit_and_its_push_expects_it()
    {
        const string Theirs = "aaaabbbbccccddddeeeeffff0000111122223333";
        const string Built = "1111222233334444555566667777888899990000";
        Parcel parcel = await TaskAsync(Project, "Build on the reviewer's update");
        string branch = ProjectClones.BranchOf(parcel.Id);
        Guid first = await HeldAsync(parcel.Id);
        await node.DispatchAsync(SoftwareSteps.Worktree, parcel.Id, first);
        Opened(await DispatchAsync(parcel.Id, first));
        await node.Worktrees.RemoveAsync(Project, first, None);
        await node.StopAsync(first);
        runner.SetRemoteBranch(Clone, branch, Theirs);
        runner.AddAncestry(ScriptedCommandRunner.PrimaryHead, Theirs);
        Guid second = await HeldAsync(parcel.Id);

        SystemStepAnswer checkedOut = await node.DispatchAsync(SoftwareSteps.Worktree, parcel.Id, second);
        runner.AddBranch(Clone, branch, Built);
        Opened(await DispatchAsync(parcel.Id, second));

        Assert.Equal(Theirs, Assert.IsType<SystemStepAnswer.Done>(checkedOut).Result.GetProperty("head").GetString());
        Assert.Contains($"push --force-with-lease=refs/heads/{branch}:{Theirs} origin {Built}:refs/heads/{branch}", runner.GitCalls);
        Assert.Equal(Built, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
    }

    [Fact]
    public async Task An_open_pull_request_whose_head_is_the_branch_is_pushed_to_and_reused_and_no_second_one_is_opened()
    {
        Parcel parcel = await TaskAsync(Project, "Reuse it");
        const string Reused = "https://github.com/example/repo/pull/5";
        github.HoldOpen(ProjectClones.BranchOf(parcel.Id), 5, Reused);

        JsonElement result = Opened(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.Equal(5, result.GetProperty("number").GetInt32());
        Assert.Equal(Reused, result.GetProperty("url").GetString());
        Assert.Contains(runner.GitCalls, c => c.StartsWith("push ", StringComparison.Ordinal));
        Assert.True(Assert.Single(github.Requests).IsLookup);
        Assert.Equal(new FieldReference("5", Reused), await FieldAsync(parcel.Id));
    }

    [Fact]
    public async Task Performing_the_step_again_after_it_opened_a_pull_request_opens_no_second_one_and_leaves_the_field_holding_the_same_one()
    {
        Parcel parcel = await TaskAsync(Project, "Twice");
        string branch = ProjectClones.BranchOf(parcel.Id);
        Guid flowRunId = await HeldAsync(parcel.Id);
        JsonElement first = Opened(await DispatchAsync(parcel.Id, flowRunId));
        long version = await VersionAsync(parcel.Id);

        JsonElement again = Opened(await DispatchAsync(parcel.Id, flowRunId));

        Assert.Equal(first.GetRawText(), again.GetRawText());
        Assert.Equal(1, github.Requests.Count(r => r.IsOpen));
        Assert.Equal([$"push --force-with-lease=refs/heads/{branch}: origin {ScriptedCommandRunner.PrimaryHead}:refs/heads/{branch}", $"push --force-with-lease=refs/heads/{branch}:{ScriptedCommandRunner.PrimaryHead} origin {ScriptedCommandRunner.PrimaryHead}:refs/heads/{branch}"], runner.GitCalls.Where(c => c.StartsWith("push ", StringComparison.Ordinal)));
        Assert.Equal(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url), await FieldAsync(parcel.Id));
        // a field already holding the pull request is not written again
        Assert.Equal(version, await VersionAsync(parcel.Id));
    }

    [Fact]
    public async Task Performing_the_step_again_after_a_push_that_GitHub_then_refused_opens_one_pull_request_and_records_it()
    {
        Parcel parcel = await TaskAsync(Project, "Pushed, then refused");
        Guid flowRunId = await HeldAsync(parcel.Id);
        github.Refuse(r => r.IsOpen, HttpStatusCode.BadGateway, "Server Error");
        Paused(await DispatchAsync(parcel.Id, flowRunId));
        github.On(r => r.IsOpen, r => FakeGitHub.Json(HttpStatusCode.Created, new JsonObject
        {
            ["number"] = FakeGitHub.Number,
            ["html_url"] = FakeGitHub.Url,
            ["head"] = new JsonObject { ["ref"] = ProjectClones.BranchOf(parcel.Id) },
        }));

        Opened(await DispatchAsync(parcel.Id, flowRunId));

        Assert.Equal(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url), await FieldAsync(parcel.Id));
    }

    [Fact]
    public async Task A_push_its_lease_refuses_pauses_saying_someone_else_updated_the_branch_with_a_lever_to_stop_the_flow_run()
    {
        Parcel parcel = await TaskAsync(Project, "Raced");
        string branch = ProjectClones.BranchOf(parcel.Id);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), Moved);
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(1,
            $"To https://github.com/example/repo.git\n ! [rejected]        {ScriptedCommandRunner.PrimaryHead} -> {branch} (stale info)\nerror: failed to push some refs to 'https://github.com/example/repo.git'"));

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.StartsWith($"Step 'open-pr' pushed nothing: someone else updated branch {branch} on the remote", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("(stale info)", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(Levers.RemoteBranchMoved("open-pr"), paused.Lever);
        Assert.StartsWith("Stop the flow run", paused.Lever, StringComparison.Ordinal);
        // a refused push moves no ref
        Assert.Equal(Moved, runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)));
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task A_push_that_fails_for_another_reason_pauses_quoting_git_with_the_perform_again_lever_and_opens_nothing()
    {
        Parcel parcel = await TaskAsync(Project, "No credentials");
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(128,
            "fatal: could not read Username for 'https://github.com': terminal prompts disabled"));

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.StartsWith("Step 'open-pr' could not push: ", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("exited with code 128", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("terminal prompts disabled", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("open-pr"), paused.Lever);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("update-ref", StringComparison.Ordinal));
        Assert.Empty(github.Requests);
    }

    [Theory]
    [InlineData(" ! [remote rejected] HEAD -> coanda/x (protected branch hook declined)", "(protected branch hook declined)")]
    [InlineData(" ! [rejected]        HEAD -> coanda/x (non-fast-forward)", "(non-fast-forward)")]
    [InlineData(" ! [rejected]        HEAD -> coanda/x (fetch first)", "(fetch first)")]
    public async Task A_push_rejected_for_a_reason_other_than_stale_info_is_no_lease_rejection_and_pauses_with_the_perform_again_lever(
        string rejection, string reason)
    {
        Parcel parcel = await TaskAsync(Project, "Rejected otherwise");
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(1, $"{rejection}\nerror: failed to push some refs"));

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.StartsWith("Step 'open-pr' could not push: ", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(reason, paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("open-pr"), paused.Lever);
    }

    [Fact]
    public async Task It_answers_opened_under_the_shipped_build_flow_which_moves_the_flow_run_to_review()
    {
        Parcel parcel = await TaskAsync(Project, "Under the shipped flow");

        SystemStepAnswer outcome = await PerformUnderAsync(ShippedFlows.Build, parcel.Id);

        Assert.Equal(new FlowRunMove.GoTo("move-to-review"), Decide(ShippedFlows.Build, Answered(outcome, OpenPrStep.OpenedVerdict)));
    }

    [Fact]
    public async Task It_answers_done_to_a_flow_run_pinned_to_the_build_flow_before_it_which_routed_open_pr_only_on_done()
    {
        Parcel parcel = await TaskAsync(Project, "Pinned to an earlier flow");

        SystemStepAnswer outcome = await PerformUnderAsync(PreviousFlows.BuildWithGatedMerge, parcel.Id);

        Assert.Equal(new FlowRunMove.GoTo("gated-merge"), Decide(PreviousFlows.BuildWithGatedMerge, Answered(outcome, SystemStepAnswer.DoneVerdict)));
        Assert.Equal(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url), await FieldAsync(parcel.Id));
    }

    [Fact]
    public async Task A_step_routing_on_both_opened_and_done_is_answered_opened()
    {
        Parcel parcel = await TaskAsync(Project, "Routes on both");
        Flow earlier = PreviousFlows.BuildWithGatedMerge;
        FlowStep openPr = earlier.Step("open-pr") ?? throw new InvalidOperationException("The flow has no open-pr step.");
        FlowTransition onDone = openPr.Transitions.Single();
        Flow both = earlier with
        {
            Steps = [.. earlier.Steps.Select(s => s.Name == "open-pr" ? s with { Transitions = [onDone, onDone with { Value = FlowValue.Of(OpenPrStep.OpenedVerdict) }] } : s)],
        };

        Answered(await PerformUnderAsync(both, parcel.Id), OpenPrStep.OpenedVerdict);
    }

    [Fact]
    public async Task A_worktree_with_changes_git_status_reports_pauses_naming_them_and_pushes_nothing()
    {
        Parcel parcel = await TaskAsync(Project, "Left something behind");
        runner.On(c => c.IsGit("status"), ScriptedCommandRunner.Ok(" M src/Changed.cs\n?? notes.txt\n"));

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.Equal("Step 'open-pr' pushed nothing: the worktree has changes that git status reports, which a push would leave out: src/Changed.cs, notes.txt.", paused.Reason);
        Assert.Equal(FlowRunLevers.PerformAgain("open-pr"), paused.Lever);
        Assert.DoesNotContain(runner.GitCalls, c => c.StartsWith("push", StringComparison.Ordinal));
        Assert.Empty(github.Requests);
    }

    public static TheoryData<HttpStatusCode, string, bool> Refusals => new()
    {
        { HttpStatusCode.Unauthorized, "Bad credentials", true },
        { HttpStatusCode.UnprocessableEntity, "Validation Failed", false },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_request_GitHub_refuses_pauses_quoting_its_status_code_and_message_and_records_no_pull_request(HttpStatusCode status, string message, bool lookup)
    {
        Parcel parcel = await TaskAsync(Project, "Refused");
        github.Refuse(
            r => lookup
                ? r.IsLookup
                : r.IsOpen,
            status,
            message);

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.StartsWith($"Step 'open-pr' pushed {ScriptedCommandRunner.PrimaryHead} but has no pull request: GitHub answered", paused.Reason, StringComparison.Ordinal);
        Assert.Contains($"status {(int)status}", paused.Reason, StringComparison.Ordinal);
        Assert.Contains(message, paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptedCommandRunner.GitHubToken, paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("open-pr"), paused.Lever);
        Assert.Null(await FieldAsync(parcel.Id));
    }

    [Fact]
    public async Task GitHub_out_of_reach_pauses_with_the_perform_again_lever()
    {
        await node.DisposeAsync();
        await StartAsync(() => new NoGitHubHandler());
        Parcel parcel = await TaskAsync(Project, "Nowhere to send it");

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.Contains("could not reach GitHub", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("no GitHub handler", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("open-pr"), paused.Lever);
    }

    [Fact]
    public async Task No_token_from_gh_pauses_and_pushes_nothing()
    {
        Parcel parcel = await TaskAsync(Project, "Not logged in");
        runner.On(c => c.Program == "gh", ScriptedCommandRunner.Fails(1, "no oauth token found for github.com"));

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id)));

        Assert.StartsWith("Step 'open-pr' pushed nothing: no GitHub token is available", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("no oauth token found", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("open-pr"), paused.Lever);
        Assert.DoesNotContain(runner.Commands, c => c.Program == "git");
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task A_gh_that_cannot_start_or_prints_nothing_is_no_token_and_pushes_nothing()
    {
        Parcel parcel = await TaskAsync(Project, "No gh");
        runner.On(c => c.Program == "gh", (c, _) => Task.FromException<CommandResult>(new CommandNotStartedException(c, "No such file or directory")));

        Guid flowRunId = await HeldAsync(parcel.Id);
        SystemStepAnswer.Paused unstarted = Paused(await DispatchAsync(parcel.Id, flowRunId));
        runner.On(c => c.Program == "gh", ScriptedCommandRunner.Ok("\n"));
        SystemStepAnswer.Paused empty = Paused(await DispatchAsync(parcel.Id, flowRunId));

        Assert.Contains("no GitHub token is available", unstarted.Reason, StringComparison.Ordinal);
        Assert.Contains("gh could not be started", unstarted.Reason, StringComparison.Ordinal);
        Assert.Contains("no GitHub token is available", empty.Reason, StringComparison.Ordinal);
        Assert.Contains("printed no single token", empty.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Commands, c => c.Program == "git");
    }

    [Fact]
    public async Task A_remote_that_is_no_github_repository_pauses_quoting_it_without_its_user_information_and_pushes_nothing()
    {
        const string Other = "beta";
        await projects.AddAsync(Principal.LocalOwner, Other, "https://someone:s3cr3t-token@gitlab.example.com/team/repo.git", "main", None);
        Parcel parcel = await TaskAsync(Other, "Elsewhere");

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id, Other), Other));

        Assert.StartsWith("Step 'open-pr' pushed nothing: the remote 'https://gitlab.example.com/team/repo.git' of project 'beta' is not a github.com repository", paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", paused.Reason, StringComparison.Ordinal);
        Assert.Equal(FlowRunLevers.PerformAgain("open-pr"), paused.Lever);
        // neither the token is read nor anything pushed
        Assert.Empty(runner.Commands);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task A_github_remote_carrying_a_credential_does_not_parse_and_the_pause_quotes_it_without_one()
    {
        const string Other = "gamma";
        await projects.AddAsync(Principal.LocalOwner, Other, "https://x-access-token:ghp_inTheRemote@github.com/example/repo.git", "main", None);
        Parcel parcel = await TaskAsync(Other, "Credential in the remote");

        SystemStepAnswer.Paused paused = Paused(await DispatchAsync(parcel.Id, await HeldAsync(parcel.Id, Other), Other));

        Assert.Contains("the remote 'https://github.com/example/repo.git' of project 'gamma'", paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_inTheRemote", paused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("x-access-token", paused.Reason, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task The_token_appears_in_no_result_or_pause_reason()
    {
        Parcel opened = await TaskAsync(Project, "Opened");
        Parcel refused = await TaskAsync(Project, "Refused");
        github.Refuse(r => r.IsLookup && r.PathAndQuery.Contains(Ids.Short(refused.Id), StringComparison.Ordinal), HttpStatusCode.Forbidden, "Resource not accessible by integration");

        SystemStepAnswer done = await DispatchAsync(opened.Id, await HeldAsync(opened.Id));
        SystemStepAnswer paused = await DispatchAsync(refused.Id, await HeldAsync(refused.Id));

        Assert.All(github.Requests, r => Assert.True(r.Authorization == $"Bearer {ScriptedCommandRunner.GitHubToken}", "the token was sent"));
        Assert.DoesNotContain(ScriptedCommandRunner.GitHubToken, Opened(done).GetRawText(), StringComparison.Ordinal);
        SystemStepAnswer.Paused pause = Paused(paused);
        Assert.DoesNotContain(ScriptedCommandRunner.GitHubToken, pause.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(ScriptedCommandRunner.GitHubToken, pause.Lever, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_push_waits_its_turn_at_the_clone_behind_a_worktree_checkout_in_the_same_project()
    {
        Parcel parcel = await TaskAsync(Project, "Wait your turn");
        Guid flowRunId = await HeldAsync(parcel.Id);
        var fetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On(c => c.IsGit("fetch"), async (_, _) =>
        {
            fetching.TrySetResult();
            await release.Task;
            return ScriptedCommandRunner.Ok();
        });

        Task<SystemStepAnswer> checkout = node.DispatchAsync(SoftwareSteps.Worktree, Ids.New(), Ids.New());
        await fetching.Task;
        Task<SystemStepAnswer> push = DispatchAsync(parcel.Id, flowRunId);
        await WhenReadAsync(runner, () => runner.Commands.Any(c => c.Program == "gh"));
        int held = runner.Commands.Count;
        await Task.Delay(TimeSpan.FromMilliseconds(100), None);

        // no git command of the push runs while the checkout holds the clone
        Assert.Equal(held, runner.Commands.Count);
        Assert.False(push.IsCompleted);
        release.SetResult();
        await checkout;
        Opened(await push);
        List<string> calls = [.. runner.GitCalls];
        int status = calls.IndexOf("status --porcelain --untracked-files=all");
        int added = calls.FindLastIndex(c => c.StartsWith("worktree add", StringComparison.Ordinal));
        Assert.True(status > added, $"Expected the status call after the worktree add at {added}, but it is at {status}.");
    }

    private static async Task WhenReadAsync(ScriptedCommandRunner runner, Func<bool> condition)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), patience.Token);
        }
    }

    private async Task<Parcel> TaskAsync(string project, string title)
    {
        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, project, "task", title, null, None);
        await parcels.BriefAsync(Principal.LocalOwner, project, parcel.Id, pullRequest: false, cancellationToken: None);
        return parcel;
    }

    private async Task<FieldReference?> FieldAsync(Guid parcelId) =>
        (await parcels.ShowAsync(Principal.LocalOwner, Project, parcelId, None))?.ValueOf(OpenPrStep.PullRequestField)?.Reference;

    private async Task<long> VersionAsync(Guid parcelId)
    {
        await using IQuerySession session = node.Store.QuerySession(Project);
        return (await session.Events.FetchStreamAsync(parcelId, token: None)).Count;
    }

    private async Task StartAsync(Func<HttpMessageHandler>? handler = null)
    {
        node = await SoftwareNode.StartAsync(runner, github, handler);
        parcels = node.Parcels;
        projects = node.Projects;
    }

    /// <summary>A flow run on the parcel sitting in its open-pr step, holding a dispatch of it, whose id it gives.</summary>
    private async Task<Guid> HeldAsync(Guid parcelId, string project = Project)
    {
        if (project != Project)
        {
            await Publishing.FlowAsync(node.Daemon, project, SoftwareNode.Steps);
        }

        (Guid flowRunId, Guid dispatchId) = await node.HeldAsync(parcelId, "open-pr", project);
        held[flowRunId] = dispatchId;
        return flowRunId;
    }

    private Task<SystemStepAnswer> DispatchAsync(Guid parcelId, Guid flowRunId, string project = Project) =>
        node.DispatchAsync(SoftwareSteps.OpenPr, parcelId, flowRunId, held[flowRunId], project);

    /// <summary>
    /// Dispatches open-pr as the daemon does for a flow run of <paramref name="flow"/> sitting in its open-pr
    /// step and holding the dispatch, where the flow is published as main wrote it, unprefixed names and all.
    /// </summary>
    private async Task<SystemStepAnswer> PerformUnderAsync(Flow flow, Guid parcelId)
    {
        await PreviousFlows.PublishAsync(node.Store, Project, flow);
        FlowRunService flowRuns = node.FlowRuns;
        var started = await flowRuns.StartAsync(Principal.LocalOwner, Project, parcelId, flow.Name, None);
        await flowRuns.StartStepAsync(Principal.LocalOwner, Project, started.Id, "open-pr", None);
        var dispatched = await flowRuns.DispatchedAsync(Principal.LocalOwner, Project, started.Id, "open-pr", None);
        FlowStep openPr = flow.Step("open-pr") ?? throw new InvalidOperationException("The flow has no open-pr step.");
        return await node.Services.GetRequiredService<FlowSystemSteps>().DispatchAsync(
            new SystemStepDispatch(
                Principal.LocalOwner, Project, started.Id, parcelId, flow, openPr, Resumed: false, Visit: 1,
                DispatchId: dispatched.Current?.DispatchId ?? Guid.Empty, WorkPath: node.Home.FlowRunWorkPath(Project, started.Id)),
            None);
    }

    private static FlowRunMove Decide(Flow flow, JsonElement answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer.GetRawText());
        var atOpenPr = new FlowRunPosition("open-pr", ImmutableDictionary<string, int>.Empty.Add("open-pr", 1), Spawns: 0);
        return Interpreter.Decide(flow, atOpenPr, document);
    }

    private static JsonElement Opened(SystemStepAnswer outcome) => Answered(outcome, OpenPrStep.OpenedVerdict);

    private static JsonElement Answered(SystemStepAnswer outcome, string verdict)
    {
        JsonElement result = Assert.IsType<SystemStepAnswer.Done>(outcome).Result;
        Assert.Equal(verdict, result.GetProperty("verdict").GetString());
        Assert.Equal(SoftwareSteps.OpenPr, result.GetProperty("systemStep").GetString());
        return result;
    }

    private static SystemStepAnswer.Paused Paused(SystemStepAnswer outcome) => Assert.IsType<SystemStepAnswer.Paused>(outcome);
}
