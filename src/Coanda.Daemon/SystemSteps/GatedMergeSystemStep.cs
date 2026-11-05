using System.Globalization;
using System.Net;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Software;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.GitHub;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// The daemon's own pre-approved merge: it inspects the pull request the task's <c>pull-request</c>
/// field names and merges exactly the head it inspected. A pull request already merged answers
/// <see cref="MergedVerdict"/> with GitHub's commit before anything read from its state or from the
/// recorded head is checked, so a step performed again after a merge that was not recorded makes no
/// second request. Otherwise it merges only an open pull request on the project's primary branch
/// whose head is the recorded head (the <c>head</c> of the latest open-pr or rebase result in the
/// chain, <see cref="RecordedHeads.OfChainAsync"/>), which is behind the primary branch by no
/// commits, which GitHub reports mergeable, and on which every check the project's record names has
/// succeeded and no other reported check has failed or is still running. The merge request carries
/// that head as the expected head and names the method the repository allows, preferring merge,
/// then squash, then rebase. The primary branch is compared with the head both before mergeability
/// and the checks are read and right before the merge; a head behind it at either comparison
/// answers <see cref="BehindVerdict"/>, whatever its checks and mergeability report, since those
/// describe a head about to be replaced. GitHub's merge takes an expected head but no expected base,
/// so a commit that lands on the primary branch between that last comparison and the merge is still
/// merged onto, a window of one request. It answers <see cref="PendingVerdict"/> with a
/// <c>reason</c> while a check or GitHub's mergeability is still to come, and pauses on everything
/// waiting or a rebase will not change. Nothing here enables GitHub's auto-merge, and nothing reads
/// the node's clone.
/// </summary>
public sealed class GatedMergeSystemStep(
    ParcelService parcels,
    ProjectService projects,
    FlowRunService flowRuns,
    GitHubToken tokens,
    Func<GitHubClient> github) : IFlowSystemStep
{
    /// <summary>The verdict the system step answers once the pull request is merged.</summary>
    public const string MergedVerdict = "merged";

    /// <summary>The verdict the system step answers while what the merge waits for is still to come.</summary>
    public const string PendingVerdict = "pending";

    /// <summary>The verdict the system step answers when the head is behind the primary branch, which the merge flow routes to a rebase.</summary>
    public const string BehindVerdict = "behind";

    public string Name => SoftwareSystemSteps.GatedMerge;

    public async ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        string step = request.Step.Name;
        Project? project = await projects.ShowAsync(request.Principal, request.Project, cancellationToken);
        if (project is null)
        {
            return PerformAgain(step, $"has no repository to merge in: {ProjectService.Unregistered(request.Project)}");
        }

        Parcel parcel = await parcels.ShowAsync(request.Principal, request.Project, request.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(request.ParcelId)} in project '{request.Project}'.");

        if (parcel.ValueOf(OpenPrSystemStep.PullRequestField)?.Reference is not { } reference)
        {
            return PerformAgain(step, $"has no pull request to merge: the task's {OpenPrSystemStep.PullRequestField} field has no value.");
        }

        if (!int.TryParse(reference.Id, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
        {
            return PerformAgain(
                step, $"has no pull request to merge: the task's {OpenPrSystemStep.PullRequestField} field holds '{reference.Id}', which is no pull request number.");
        }

        if (GitHubRepository.Parse(project.Remote) is not { } repository)
        {
            return PerformAgain(
                step,
                $"merged nothing: the remote '{GitHubRepository.Redact(project.Remote)}' of project '{project.Name}' is not a github.com repository in a form the step reads ({GitHubRepository.Forms}).");
        }

        string token;
        try
        {
            token = await tokens.ReadAsync(cancellationToken);
        }
        catch (GitHubTokenUnavailableException missing)
        {
            return PerformAgain(step, $"merged nothing: no GitHub token is available, because {missing.Message}");
        }

        GitHubClient client = github();
        try
        {
            return await InspectAndMergeAsync(request, project, repository, number, client, token, cancellationToken);
        }
        catch (GitHubRefusedException refused)
        {
            return PerformAgain(step, $"merged nothing: {refused.Message}");
        }
        catch (HttpRequestException unreached)
        {
            return PerformAgain(step, $"merged nothing: it could not reach GitHub: {unreached.Message}");
        }
    }

    private async Task<SystemStepAnswer> InspectAndMergeAsync(
        SystemStepDispatch request,
        Project project,
        GitHubRepository repository,
        int number,
        GitHubClient client,
        string token,
        CancellationToken cancellationToken)
    {
        string step = request.Step.Name;
        string named = $"pull request #{number} in {repository}";
        GitHubPullRequestState pullRequest = await client.PullRequestAsync(repository, number, token, cancellationToken);

        // Checked before every guard: a resume after a merge that was not recorded finds it merged,
        // whatever its head or base has become since.
        if (pullRequest.Merged)
        {
            return pullRequest.MergeCommit is { Length: > 0 } commit
                ? Merged(commit)
                : PerformAgain(step, $"found {named} merged, but GitHub reports no commit for the merge.");
        }

        if (await RecordedHeads.OfChainAsync(flowRuns, request, cancellationToken) is not { } head)
        {
            return PerformAgain(
                step, $"merged nothing: no open-pr or rebase result with a head is recorded in the flow run's chain, so there is no pushed commit to hold {named} to.");
        }

        if (project.RequiredChecks.Count == 0)
        {
            return PerformAgain(
                step,
                $"merged nothing: project '{project.Name}' names no check for a merge to wait for, so the step cannot tell whether every expected check has reported; name them with coa project set {project.Name} --required-check <check>.");
        }

        if (!pullRequest.IsOpen)
        {
            return PerformAgain(step, $"merged nothing: {named} is closed without being merged.");
        }

        if (!string.Equals(pullRequest.BaseRef, project.PrimaryBranch, StringComparison.Ordinal))
        {
            return PerformAgain(
                step, $"merged nothing: {named} targets '{pullRequest.BaseRef}', not the primary branch '{project.PrimaryBranch}' of project '{project.Name}'.");
        }

        if (!string.Equals(pullRequest.HeadSha, head, StringComparison.Ordinal))
        {
            return PerformAgain(
                step, $"merged nothing: the head of {named} is {pullRequest.HeadSha}, not {head}, the commit the flow run's chain recorded as pushed; a commit nobody reviewed is never merged.");
        }

        if (await BehindAsync() is { } early)
        {
            return early;
        }

        if (pullRequest.Mergeable is false)
        {
            return PerformAgain(step, $"merged nothing: GitHub reports that {named} has a conflict with '{project.PrimaryBranch}'.");
        }

        IReadOnlyList<GitHubCheckRun> runs = await client.CheckRunsAsync(repository, head, token, cancellationToken);
        IReadOnlyList<GitHubCommitStatus> statuses = await client.StatusesAsync(repository, head, token, cancellationToken);
        Checks checks = Inspect(project.RequiredChecks, runs, statuses);
        if (checks.Failed is { } failed)
        {
            return PerformAgain(step, $"merged nothing: {failed} on {head}.");
        }

        List<string> waiting = [.. checks.Pending];
        if (pullRequest.Mergeable is null)
        {
            waiting.Add($"GitHub has not yet computed whether {named} is mergeable");
        }

        if (waiting.Count > 0)
        {
            return SystemStepAnswer.ResultWith(PendingVerdict, Name, ("reason", $"Waiting on {named}: {string.Join("; ", waiting)}."));
        }

        GitHubMergeMethods methods = await client.MergeMethodsAsync(repository, token, cancellationToken);
        if (methods.Preferred is not { } method)
        {
            return PerformAgain(step, $"merged nothing: {repository} allows none of the merge methods merge, squash, and rebase.");
        }

        // The primary branch is compared again right before the merge, because the checks and the
        // methods took requests of their own while it could move. GitHub's merge takes no expected
        // base, so a base that moves between this comparison and the merge is still merged onto.
        if (await BehindAsync() is { } late)
        {
            return late;
        }

        try
        {
            return Merged(await client.MergeAsync(repository, number, head, method, token, cancellationToken));
        }
        catch (GitHubRefusedException refused) when (refused.Status == HttpStatusCode.Conflict)
        {
            return PerformAgain(
                step, $"merged nothing: GitHub refused the merge because the head of {named} moved after it was inspected: {refused.Message}");
        }

        async Task<SystemStepAnswer.Done?> BehindAsync()
        {
            int behind = await client.BehindByAsync(repository, project.PrimaryBranch, head, token, cancellationToken);
            return behind > 0
                ? SystemStepAnswer.ResultWith(
                    BehindVerdict,
                    Name,
                    ("reason", $"The head {head} of {named} is behind the primary branch '{project.PrimaryBranch}' by {behind} commit{(behind == 1 ? string.Empty : "s")}, so a merge would land a tree CI never ran."),
                    ("behind", behind))
                : null;
        }
    }

    /// <summary>What the checks on the head say: the first failure, or the checks still to come.</summary>
    private readonly record struct Checks(string? Failed, IReadOnlyList<string> Pending);

    /// <summary>
    /// Judges the checks reported on the head. A check run counts by the latest run of its name and a
    /// status by the latest status of its context. A named check fails when its latest run completed
    /// with anything but success or its latest status is failure or error, succeeds when its latest
    /// run completed with success or its latest status is success, and is pending otherwise, which
    /// includes not yet being reported. Any other reported check fails when its latest run completed
    /// with anything but success, neutral, or skipped, or its latest status is failure or error, and
    /// is pending while its run has not completed or its status is pending. Names compare ordinally.
    /// </summary>
    private static Checks Inspect(IReadOnlyList<string> required, IReadOnlyList<GitHubCheckRun> runs, IReadOnlyList<GitHubCommitStatus> statuses)
    {
        Dictionary<string, GitHubCheckRun> latestRuns = runs
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Id).First(), StringComparer.Ordinal);
        Dictionary<string, GitHubCommitStatus> latestStatuses = statuses
            .GroupBy(s => s.Context, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Id).First(), StringComparer.Ordinal);
        var named = new HashSet<string>(required, StringComparer.Ordinal);
        List<string> pending = [];

        foreach (string check in required.Distinct(StringComparer.Ordinal))
        {
            GitHubCheckRun? run = latestRuns.GetValueOrDefault(check);
            GitHubCommitStatus? status = latestStatuses.GetValueOrDefault(check);
            string? failed = (run, status) switch
            {
                ({ IsCompleted: true, Conclusion: not "success" } r, _) => $"the required check '{check}' completed with the conclusion '{r.Conclusion ?? "none"}'",
                (_, { State: "failure" or "error" } s) => $"the required check '{check}' reported the status '{s.State}'",
                _ => null,
            };
            if (failed is not null)
            {
                return new Checks(failed, []);
            }

            bool succeeded = run is { IsCompleted: true, Conclusion: "success" } || status is { State: "success" };
            string? waiting = (run, status) switch
            {
                _ when succeeded => null,
                (null, null) => $"the required check '{check}' has not been reported",
                ({ IsCompleted: false } r, _) => $"the required check '{check}' is {r.Status}",
                (_, { } s) => $"the required check '{check}' is {s.State}",
                _ => $"the required check '{check}' has not succeeded",
            };
            if (waiting is not null)
            {
                pending.Add(waiting);
            }
        }

        foreach (GitHubCheckRun run in latestRuns.Values.Where(r => !named.Contains(r.Name)).OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (run is { IsCompleted: true, Conclusion: not ("success" or "neutral" or "skipped") })
            {
                return new Checks($"the check '{run.Name}' completed with the conclusion '{run.Conclusion ?? "none"}'", []);
            }

            if (!run.IsCompleted)
            {
                pending.Add($"the check '{run.Name}' is {run.Status}");
            }
        }

        foreach (GitHubCommitStatus status in latestStatuses.Values.Where(s => !named.Contains(s.Context)).OrderBy(s => s.Context, StringComparer.Ordinal))
        {
            switch (status.State)
            {
                case "failure" or "error":
                    return new Checks($"the check '{status.Context}' reported the status '{status.State}'", []);
                case not "success":
                    pending.Add($"the check '{status.Context}' is {status.State}");
                    break;
            }
        }

        return new Checks(null, pending);
    }

    private SystemStepAnswer.Done Merged(string commit) => SystemStepAnswer.ResultWith(MergedVerdict, Name, ("commit", commit));

    private static SystemStepAnswer.Paused PerformAgain(string step, string what) =>
        new($"Step '{step}' {what}", FlowRunLevers.PerformAgain(step));
}
