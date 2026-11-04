using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Commands;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.GitHub;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;
using Coanda.Tests.Core;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.GitHub;
using Fisher;
using Microsoft.Extensions.Logging.Abstractions;

namespace Coanda.Tests.Daemon.Actions;

public sealed class OpenPrActionTests : IAsyncLifetime
{
    private const string Project = "alpha";
    private const string Moved = "fedcba9876543210fedcba9876543210fedcba98";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal FlowRunPrincipal = new("flow-run:under-test", Role.Agent);
    private static readonly ContourStep Step = new() { Name = "open-pr", Action = SoftwareActions.OpenPr };

    private readonly TemporaryHome temp = new();
    private readonly ScriptedCommandRunner runner = new();
    private readonly FakeGitHub github = new();
    private IDocumentStore store = null!;
    private ParcelService parcels = null!;
    private ProjectService projects = null!;
    private ProjectClones clones = null!;
    private OpenPrAction action = null!;

    private string Clone => temp.Home.ProjectClonePath(Project);

    public async Task InitializeAsync()
    {
        store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        parcels = new ParcelService(store, TimeProvider.System);
        projects = new ProjectService(store, TimeProvider.System);
        clones = new ProjectClones(temp.Home, runner, NullLogger<ProjectClones>.Instance);
        action = new OpenPrAction(parcels, projects, clones, new GitHubToken(runner, temp.Home), github.Client);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        temp.Dispose();
    }

    [Fact]
    public async Task A_first_run_pushes_the_branch_expecting_it_absent_opens_a_pull_request_against_the_primary_and_records_it_on_the_task()
    {
        Parcel parcel = await TaskAsync(Project, "Open the pull request");
        string branch = ProjectClones.BranchOf(parcel.Id);
        Guid flowRunId = Ids.New();
        runner.AddBranch(Clone, branch, Moved);

        JsonElement result = Opened(await PerformAsync(parcel.Id, flowRunId));

        result.GetProperty("number").GetInt32().Should().Be(FakeGitHub.Number);
        result.GetProperty("url").GetString().Should().Be(FakeGitHub.Url);
        result.GetProperty("head").GetString().Should().Be(Moved);
        runner.Commands[0].Should().Match<CommandLine>(c => c.Program == "gh" && string.Join(' ', c.Arguments) == "auth token --hostname github.com");
        runner.Commands.Single(c => c.IsGit("status")).WorkingDirectory.Should().Be(temp.Home.FlowRunWorkPath(Project, flowRunId));
        runner.GitCalls.Should().Equal(
            "status --porcelain --untracked-files=all",
            $"rev-parse --verify --quiet refs/heads/{branch}^{{commit}}",
            $"for-each-ref --format=%(objectname) refs/remotes/origin/{branch}",
            $"push --force-with-lease=refs/heads/{branch}: origin {Moved}:refs/heads/{branch}",
            $"update-ref refs/remotes/origin/{branch} {Moved}");
        runner.Commands.Where(c => c.Program == "git" && !c.IsGit("status")).Should().OnlyContain(c => c.GitDirectory() == Clone);
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Moved, "the next push on the branch expects what this one pushed");

        github.Requests.Select(r => $"{r.Method} {r.PathAndQuery}").Should().Equal(
            $"GET /repos/example/repo/pulls?state=open&head=example%3Acoanda%2F{Ids.Short(parcel.Id)}",
            "POST /repos/example/repo/pulls");
        github.Requests.Should().OnlyContain(r => r.Uri.Host == "api.github.com" && r.Authorization == $"Bearer {ScriptedCommandRunner.GitHubToken}");
        JsonNode body = JsonNode.Parse(github.Requests[1].Body ?? "{}") ?? new JsonObject();
        ((string?)body["title"]).Should().Be("Open the pull request");
        ((string?)body["head"]).Should().Be(branch);
        ((string?)body["base"]).Should().Be(RegisteredProjects.Branch);
        ((string?)body["body"]).Should().Be(
            $"{TaskFields.ObjectiveValue.Text}\n\n## Criteria\n\n{string.Concat((TaskFields.CriteriaValue.Items ?? []).Select(c => $"- {c}\n"))}");

        (await FieldAsync(parcel.Id)).Should().Be(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url));
    }

    [Fact]
    public async Task A_clone_holding_the_branchs_remote_tracking_ref_pushes_with_that_commit_as_the_lease_and_moves_the_ref_to_the_pushed_one()
    {
        Parcel parcel = await TaskAsync(Project, "Push again");
        string branch = ProjectClones.BranchOf(parcel.Id);
        runner.AddBranch(Clone, branch, Moved);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), ScriptedCommandRunner.PrimaryHead);

        Opened(await PerformAsync(parcel.Id, Ids.New()));

        runner.GitCalls.Should().Contain($"push --force-with-lease=refs/heads/{branch}:{ScriptedCommandRunner.PrimaryHead} origin {Moved}:refs/heads/{branch}");
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Moved);
    }

    [Fact]
    public async Task A_new_flow_run_after_someone_updated_the_branch_on_the_remote_builds_on_their_commit_and_its_push_expects_it()
    {
        const string Theirs = "aaaabbbbccccddddeeeeffff0000111122223333";
        const string Built = "1111222233334444555566667777888899990000";
        var worktree = new WorktreeAction(projects, clones, new FlowRunService(store, TimeProvider.System), new FlowRunWorkers());
        ContourStep checkoutStep = Step with { Name = "worktree", Action = SoftwareActions.Worktree };
        Parcel parcel = await TaskAsync(Project, "Build on the reviewer's update");
        string branch = ProjectClones.BranchOf(parcel.Id);
        Guid first = Ids.New();
        await worktree.PerformAsync(new ActionRequest(FlowRunPrincipal, Project, first, parcel.Id, ShippedContours.Build, checkoutStep, false), None);
        Opened(await PerformAsync(parcel.Id, first));
        await clones.RemoveAsync(Project, first, None);
        runner.SetRemoteBranch(Clone, branch, Theirs);
        runner.AddAncestry(ScriptedCommandRunner.PrimaryHead, Theirs);
        Guid second = Ids.New();

        ActionOutcome checkedOut = await worktree.PerformAsync(new ActionRequest(FlowRunPrincipal, Project, second, parcel.Id, ShippedContours.Build, checkoutStep, false), None);
        runner.AddBranch(Clone, branch, Built);
        Opened(await PerformAsync(parcel.Id, second));

        checkedOut.Should().BeOfType<ActionOutcome.Done>().Which.Result.GetProperty("head").GetString().Should().Be(Theirs);
        runner.GitCalls.Should().Contain($"push --force-with-lease=refs/heads/{branch}:{Theirs} origin {Built}:refs/heads/{branch}");
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Built);
    }

    [Fact]
    public async Task An_open_pull_request_whose_head_is_the_branch_is_pushed_to_and_reused_and_no_second_one_is_opened()
    {
        Parcel parcel = await TaskAsync(Project, "Reuse it");
        const string Reused = "https://github.com/example/repo/pull/5";
        github.HoldOpen(ProjectClones.BranchOf(parcel.Id), 5, Reused);

        JsonElement result = Opened(await PerformAsync(parcel.Id, Ids.New()));

        result.GetProperty("number").GetInt32().Should().Be(5);
        result.GetProperty("url").GetString().Should().Be(Reused);
        runner.GitCalls.Should().Contain(c => c.StartsWith("push ", StringComparison.Ordinal));
        github.Requests.Should().ContainSingle().Which.IsLookup.Should().BeTrue();
        (await FieldAsync(parcel.Id)).Should().Be(new FieldReference("5", Reused));
    }

    [Fact]
    public async Task Performing_the_step_again_after_it_opened_a_pull_request_opens_no_second_one_and_leaves_the_field_holding_the_same_one()
    {
        Parcel parcel = await TaskAsync(Project, "Twice");
        string branch = ProjectClones.BranchOf(parcel.Id);
        Guid flowRunId = Ids.New();
        JsonElement first = Opened(await PerformAsync(parcel.Id, flowRunId));
        long version = await VersionAsync(parcel.Id);

        JsonElement again = Opened(await PerformAsync(parcel.Id, flowRunId));

        again.GetRawText().Should().Be(first.GetRawText());
        github.Requests.Count(r => r.IsOpen).Should().Be(1);
        runner.GitCalls.Where(c => c.StartsWith("push ", StringComparison.Ordinal)).Should().Equal(
            $"push --force-with-lease=refs/heads/{branch}: origin {ScriptedCommandRunner.PrimaryHead}:refs/heads/{branch}",
            $"push --force-with-lease=refs/heads/{branch}:{ScriptedCommandRunner.PrimaryHead} origin {ScriptedCommandRunner.PrimaryHead}:refs/heads/{branch}");
        (await FieldAsync(parcel.Id)).Should().Be(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url));
        (await VersionAsync(parcel.Id)).Should().Be(version, "a field already holding the pull request is not written again");
    }

    [Fact]
    public async Task Performing_the_step_again_after_a_push_that_GitHub_then_refused_opens_one_pull_request_and_records_it()
    {
        Parcel parcel = await TaskAsync(Project, "Pushed, then refused");
        Guid flowRunId = Ids.New();
        github.Refuse(r => r.IsOpen, HttpStatusCode.BadGateway, "Server Error");
        Paused(await PerformAsync(parcel.Id, flowRunId));
        github.On(r => r.IsOpen, r => FakeGitHub.Json(HttpStatusCode.Created, new JsonObject
        {
            ["number"] = FakeGitHub.Number,
            ["html_url"] = FakeGitHub.Url,
            ["head"] = new JsonObject { ["ref"] = ProjectClones.BranchOf(parcel.Id) },
        }));

        Opened(await PerformAsync(parcel.Id, flowRunId));

        (await FieldAsync(parcel.Id)).Should().Be(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url));
    }

    [Fact]
    public async Task A_push_its_lease_refuses_pauses_saying_someone_else_updated_the_branch_with_a_lever_to_stop_the_flow_run()
    {
        Parcel parcel = await TaskAsync(Project, "Raced");
        string branch = ProjectClones.BranchOf(parcel.Id);
        runner.SetTrackingRef(Clone, ProjectClones.TrackingRefOf(branch), Moved);
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(1,
            $"To https://github.com/example/repo.git\n ! [rejected]        {ScriptedCommandRunner.PrimaryHead} -> {branch} (stale info)\nerror: failed to push some refs to 'https://github.com/example/repo.git'"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New()));

        paused.Reason.Should().StartWith($"Step 'open-pr' pushed nothing: someone else updated branch {branch} on the remote").And.Contain("(stale info)");
        paused.Lever.Should().Be(FlowRunLevers.RemoteBranchMoved("open-pr"));
        paused.Lever.Should().StartWith("Stop the flow run");
        runner.TrackingRef(Clone, ProjectClones.TrackingRefOf(branch)).Should().Be(Moved, "a refused push moves no ref");
        github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_push_that_fails_for_another_reason_pauses_quoting_git_with_the_perform_again_lever_and_opens_nothing()
    {
        Parcel parcel = await TaskAsync(Project, "No credentials");
        runner.On(c => c.IsGit("push"), ScriptedCommandRunner.Fails(128,
            "fatal: could not read Username for 'https://github.com': terminal prompts disabled"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New()));

        paused.Reason.Should().StartWith("Step 'open-pr' could not push: ").And.Contain("exited with code 128").And.Contain("terminal prompts disabled");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("open-pr"));
        runner.GitCalls.Should().NotContain(c => c.StartsWith("update-ref", StringComparison.Ordinal));
        github.Requests.Should().BeEmpty();
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

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New()));

        paused.Reason.Should().StartWith("Step 'open-pr' could not push: ").And.Contain(reason);
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("open-pr"));
    }

    [Fact]
    public async Task It_answers_opened_under_the_shipped_build_contour_which_moves_the_flow_run_to_review()
    {
        Parcel parcel = await TaskAsync(Project, "Under the shipped contour");

        ActionOutcome outcome = await PerformUnderAsync(ShippedContours.Build, parcel.Id);

        Decide(ShippedContours.Build, Answered(outcome, OpenPrAction.OpenedVerdict)).Should().Be(new FlowRunMove.GoTo("move-to-review"));
    }

    [Fact]
    public async Task It_answers_done_to_a_flow_run_pinned_to_the_build_contour_before_it_which_routed_open_pr_only_on_done()
    {
        Parcel parcel = await TaskAsync(Project, "Pinned to an earlier contour");

        ActionOutcome outcome = await PerformUnderAsync(PreviousContours.BuildWithGatedMerge, parcel.Id);

        Decide(PreviousContours.BuildWithGatedMerge, Answered(outcome, ActionOutcome.DoneVerdict)).Should().Be(new FlowRunMove.GoTo("gated-merge"));
        (await FieldAsync(parcel.Id)).Should().Be(new FieldReference(FakeGitHub.Number.ToString(), FakeGitHub.Url));
    }

    [Fact]
    public void A_step_routing_on_both_opened_and_done_is_answered_opened()
    {
        ContourStep both = Step with
        {
            Transitions =
            [
                new ContourTransition { Field = ActionOutcome.Verdict, Value = ContourValue.Of("done"), Target = "a" },
                new ContourTransition { Field = ActionOutcome.Verdict, Value = ContourValue.Of("opened"), Target = "b" },
            ],
        };

        OpenPrAction.VerdictFor(both).Should().Be(OpenPrAction.OpenedVerdict);
        OpenPrAction.VerdictFor(Step).Should().Be(OpenPrAction.OpenedVerdict);
    }

    [Fact]
    public async Task A_worktree_with_changes_git_status_reports_pauses_naming_them_and_pushes_nothing()
    {
        Parcel parcel = await TaskAsync(Project, "Left something behind");
        runner.On(c => c.IsGit("status"), ScriptedCommandRunner.Ok(" M src/Changed.cs\n?? notes.txt\n"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New()));

        paused.Reason.Should().Be("Step 'open-pr' pushed nothing: the worktree has changes that git status reports, which a push would leave out: src/Changed.cs, notes.txt.");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("open-pr"));
        runner.GitCalls.Should().NotContain(c => c.StartsWith("push", StringComparison.Ordinal));
        github.Requests.Should().BeEmpty();
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

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New()));

        paused.Reason.Should().StartWith($"Step 'open-pr' pushed {ScriptedCommandRunner.PrimaryHead} but has no pull request: GitHub answered")
            .And.Contain($"status {(int)status}").And.Contain(message);
        paused.Reason.Should().NotContain(ScriptedCommandRunner.GitHubToken);
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("open-pr"));
        (await FieldAsync(parcel.Id)).Should().BeNull();
    }

    [Fact]
    public async Task GitHub_out_of_reach_pauses_with_the_perform_again_lever()
    {
        action = new OpenPrAction(parcels, projects, clones, new GitHubToken(runner, temp.Home), () => new GitHubClient(new HttpClient(new NoGitHubHandler())));
        Parcel parcel = await TaskAsync(Project, "Nowhere to send it");

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New()));

        paused.Reason.Should().Contain("could not reach GitHub").And.Contain("no GitHub handler");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("open-pr"));
    }

    [Fact]
    public async Task No_token_from_gh_pauses_and_pushes_nothing()
    {
        Parcel parcel = await TaskAsync(Project, "Not logged in");
        runner.On(c => c.Program == "gh", ScriptedCommandRunner.Fails(1, "no oauth token found for github.com"));

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New()));

        paused.Reason.Should().StartWith("Step 'open-pr' pushed nothing: no GitHub token is available").And.Contain("no oauth token found");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("open-pr"));
        runner.Commands.Should().NotContain(c => c.Program == "git");
        github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_gh_that_cannot_start_or_prints_nothing_is_no_token_and_pushes_nothing()
    {
        Parcel parcel = await TaskAsync(Project, "No gh");
        runner.On(c => c.Program == "gh", (c, _) => Task.FromException<CommandResult>(new CommandNotStartedException(c, "No such file or directory")));

        ActionOutcome.Paused unstarted = Paused(await PerformAsync(parcel.Id, Ids.New()));
        runner.On(c => c.Program == "gh", ScriptedCommandRunner.Ok("\n"));
        ActionOutcome.Paused empty = Paused(await PerformAsync(parcel.Id, Ids.New()));

        unstarted.Reason.Should().Contain("no GitHub token is available").And.Contain("gh could not be started");
        empty.Reason.Should().Contain("no GitHub token is available").And.Contain("printed no single token");
        runner.Commands.Should().NotContain(c => c.Program == "git");
    }

    [Fact]
    public async Task A_remote_that_is_no_github_repository_pauses_quoting_it_without_its_user_information_and_pushes_nothing()
    {
        const string Other = "beta";
        await projects.AddAsync(Principal.LocalOwner, Other, "https://someone:s3cr3t-token@gitlab.example.com/team/repo.git", "main", None);
        Parcel parcel = await TaskAsync(Other, "Elsewhere");

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New(), Other));

        paused.Reason.Should().StartWith("Step 'open-pr' pushed nothing: the remote 'https://gitlab.example.com/team/repo.git' of project 'beta' is not a github.com repository");
        paused.Reason.Should().NotContain("s3cr3t").And.NotContain("someone");
        paused.Lever.Should().Be(FlowRunLevers.PerformAgain("open-pr"));
        runner.Commands.Should().BeEmpty("neither the token is read nor anything pushed");
        github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_github_remote_carrying_a_credential_does_not_parse_and_the_pause_quotes_it_without_one()
    {
        const string Other = "gamma";
        await projects.AddAsync(Principal.LocalOwner, Other, "https://x-access-token:ghp_inTheRemote@github.com/example/repo.git", "main", None);
        Parcel parcel = await TaskAsync(Other, "Credential in the remote");

        ActionOutcome.Paused paused = Paused(await PerformAsync(parcel.Id, Ids.New(), Other));

        paused.Reason.Should().Contain("the remote 'https://github.com/example/repo.git' of project 'gamma'");
        paused.Reason.Should().NotContain("ghp_inTheRemote").And.NotContain("x-access-token");
        runner.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task The_token_appears_in_no_result_or_pause_reason()
    {
        Parcel opened = await TaskAsync(Project, "Opened");
        Parcel refused = await TaskAsync(Project, "Refused");
        github.Refuse(r => r.IsLookup && r.PathAndQuery.Contains(Ids.Short(refused.Id), StringComparison.Ordinal), HttpStatusCode.Forbidden, "Resource not accessible by integration");

        ActionOutcome done = await PerformAsync(opened.Id, Ids.New());
        ActionOutcome paused = await PerformAsync(refused.Id, Ids.New());

        github.Requests.Should().OnlyContain(r => r.Authorization == $"Bearer {ScriptedCommandRunner.GitHubToken}", "the token was sent");
        Opened(done).GetRawText().Should().NotContain(ScriptedCommandRunner.GitHubToken);
        ActionOutcome.Paused pause = Paused(paused);
        pause.Reason.Should().NotContain(ScriptedCommandRunner.GitHubToken);
        pause.Lever.Should().NotContain(ScriptedCommandRunner.GitHubToken);
    }

    [Fact]
    public async Task The_push_waits_its_turn_at_the_clone_behind_a_worktree_checkout_in_the_same_project()
    {
        var worktree = new WorktreeAction(projects, clones, new FlowRunService(store, TimeProvider.System), new FlowRunWorkers());
        Parcel parcel = await TaskAsync(Project, "Wait your turn");
        var fetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On(c => c.IsGit("fetch"), async (_, _) =>
        {
            fetching.TrySetResult();
            await release.Task;
            return ScriptedCommandRunner.Ok();
        });

        Task<ActionOutcome> checkout = worktree.PerformAsync(
            new ActionRequest(FlowRunPrincipal, Project, Ids.New(), Ids.New(), ShippedContours.Build, Step with { Name = "worktree", Action = SoftwareActions.Worktree }, false), None).AsTask();
        await fetching.Task;
        Task<ActionOutcome> push = PerformAsync(parcel.Id, Ids.New()).AsTask();
        await WhenReadAsync(runner, () => runner.Commands.Any(c => c.Program == "gh"));
        int held = runner.Commands.Count;
        await Task.Delay(TimeSpan.FromMilliseconds(100), None);

        runner.Commands.Should().HaveCount(held, "no git command of the push runs while the checkout holds the clone");
        push.IsCompleted.Should().BeFalse();
        release.SetResult();
        await checkout;
        Opened(await push);
        List<string> calls = [.. runner.GitCalls];
        calls.IndexOf("status --porcelain --untracked-files=all").Should().BeGreaterThan(calls.FindLastIndex(c => c.StartsWith("worktree add", StringComparison.Ordinal)));
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
        (await parcels.ShowAsync(Principal.LocalOwner, Project, parcelId, None))?.ValueOf(OpenPrAction.PullRequestField)?.Reference;

    private async Task<long> VersionAsync(Guid parcelId)
    {
        await using IQuerySession session = store.QuerySession(Project);
        return (await session.Events.FetchStreamAsync(parcelId, token: None)).Count;
    }

    private ValueTask<ActionOutcome> PerformAsync(Guid parcelId, Guid flowRunId, string project = Project) =>
        action.PerformAsync(new ActionRequest(FlowRunPrincipal, project, flowRunId, parcelId, ShippedContours.Build, Step, Resumed: false), None);

    private ValueTask<ActionOutcome> PerformUnderAsync(Contour contour, Guid parcelId)
    {
        ContourStep openPr = contour.Step("open-pr") ?? throw new InvalidOperationException("The contour has no open-pr step.");
        return action.PerformAsync(new ActionRequest(FlowRunPrincipal, Project, Ids.New(), parcelId, contour, openPr, Resumed: false), None);
    }

    private static FlowRunMove Decide(Contour contour, JsonElement answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer.GetRawText());
        var atOpenPr = new FlowRunPosition("open-pr", ImmutableDictionary<string, int>.Empty.Add("open-pr", 1), Spawns: 0);
        return Interpreter.Decide(contour, atOpenPr, document);
    }

    private static JsonElement Opened(ActionOutcome outcome) => Answered(outcome, OpenPrAction.OpenedVerdict);

    private static JsonElement Answered(ActionOutcome outcome, string verdict)
    {
        JsonElement result = outcome.Should().BeOfType<ActionOutcome.Done>().Subject.Result;
        result.GetProperty("verdict").GetString().Should().Be(verdict);
        result.GetProperty("action").GetString().Should().Be(SoftwareActions.OpenPr);
        return result;
    }

    private static ActionOutcome.Paused Paused(ActionOutcome outcome) =>
        outcome.Should().BeOfType<ActionOutcome.Paused>().Subject;
}
