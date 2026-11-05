using System.Globalization;
using System.Text;
using Coanda.Modules.Software.GitHub;
using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// Pushes the flow run's branch to the remote of the repo the task works in
/// (<see cref="StepTargets.TargetAsync"/>) and opens a pull request from it against that repo's primary
/// branch, or reuses the open one whose head it already is, then records that pull request
/// in the task's <c>pull-request</c> field, under the dispatch it holds, and answers
/// <see cref="OpenedVerdict"/> with its <c>number</c> and <c>url</c> and the <c>head</c> pushed. The
/// remote is parsed and the token read before anything is pushed, so a flow run that cannot open a pull
/// request pushes nothing. The token is read through the credential profile selected for the module in
/// the project (<see cref="GitHubToken"/>), and a selected profile that yields none pauses with a lever
/// naming that setting; the push itself uses this node's own git credentials whatever profile is selected. A worktree with changes, a remote that is no github.com repository, no
/// token, no usable clone of the repo, a failed push, and a request GitHub refuses each pause with the perform-again lever; a push
/// the lease refuses pauses with a lever to stop the flow run, because performing the step again would
/// be refused the same way.
/// </summary>
public sealed class OpenPrStep(ProjectClones clones, GitHubToken tokens, Func<GitHubClient> github) : ISoftwareStep
{
    /// <summary>The verdict the step answers once the pull request is open.</summary>
    public const string OpenedVerdict = "opened";

    /// <summary>The task field the pull request is recorded in.</summary>
    public const string PullRequestField = "pull-request";

    public string Name => SoftwareSteps.OpenPr;

    public async Task<DispatchAnswer> DispatchAsync(StepContext context, CancellationToken cancellationToken)
    {
        DispatchRequest request = context.Request;
        CoreProject project;
        try
        {
            project = await context.Core.ProjectAsync(request.Project, cancellationToken);
        }
        catch (CoreApiException missing)
        {
            return await context.PerformAgainAsync($"has no repository to open a pull request in: {missing.Message}", cancellationToken);
        }

        if ((await StepTargets.TargetAsync(context, project, cancellationToken)).Lacks(out RepoTarget? target, out DispatchAnswer? pause))
        {
            return pause;
        }

        CoreParcel parcel = await context.ParcelAsync(cancellationToken);
        if (GitHubRepository.Parse(target.Remote) is not { } repository)
        {
            return await context.PerformAgainAsync(
                $"pushed nothing: the remote '{GitHubRepository.Redact(target.Remote)}' of repo '{target.Repo}' in project '{project.Name}' is not a github.com repository in a form the step reads ({GitHubRepository.Forms}).",
                cancellationToken);
        }

        string token;
        try
        {
            token = await tokens.ReadAsync((await context.SettingsAsync(cancellationToken)).CredentialProfile, cancellationToken);
        }
        catch (GitHubProfileTokenException failing)
        {
            return await context.PausedAsync(
                $"pushed nothing: no GitHub token is available, because {failing.Message}",
                step => Levers.SelectProfile(step, project.Name, failing.TokenPath),
                cancellationToken);
        }
        catch (GitHubTokenUnavailableException missing)
        {
            return await context.PerformAgainAsync($"pushed nothing: no GitHub token is available, because {missing.Message}", cancellationToken);
        }

        Push push = await clones.PushAsync(target, request.ParcelId, request.WorkPath, cancellationToken);
        string head;
        switch (push)
        {
            case Push.Pushed pushed:
                head = pushed.Head;
                break;
            case Push.Unclean unclean:
                return await context.PerformAgainAsync(
                    $"pushed nothing: the worktree has changes that git status reports, which a push would leave out: {string.Join(", ", unclean.Changes)}.",
                    cancellationToken);
            case Push.Stale stale:
                string step = await context.StepNameAsync(cancellationToken);
                return new DispatchAnswer.Paused(
                    $"Step '{step}' pushed nothing: someone else updated branch {ProjectClones.BranchOf(request.ParcelId)} on the remote since the node last pushed or fetched it, so git refused the push its lease guards. {stale.Reason}",
                    Levers.RemoteBranchMoved(step));
            case Push.Failed failed:
                return await context.PerformAgainAsync($"could not push: {failed.Reason}", cancellationToken);
            case Push.NoClone missing:
                return await context.PerformAgainAsync($"pushed nothing: {missing.Reason}", cancellationToken);
            default:
                throw new InvalidOperationException($"A push ended in a way the open-pr step does not know: {push.GetType().Name}.");
        }

        string branch = ProjectClones.BranchOf(request.ParcelId);
        GitHubClient client = github();
        GitHubPullRequest pullRequest;
        try
        {
            pullRequest = await client.FindOpenAsync(repository, branch, token, cancellationToken)
                ?? await client.OpenAsync(repository, branch, target.PrimaryBranch, parcel.Title, Body(parcel), token, cancellationToken);
        }
        catch (GitHubRefusedException refused)
        {
            return await context.PerformAgainAsync($"pushed {head} but has no pull request: {refused.Message}", cancellationToken);
        }
        catch (HttpRequestException unreached)
        {
            return await context.PerformAgainAsync($"pushed {head} but could not reach GitHub: {unreached.Message}", cancellationToken);
        }

        string number = pullRequest.Number.ToString(CultureInfo.InvariantCulture);
        CoreFieldValue recorded = CoreFieldValue.OfReference(number, pullRequest.Url);
        if (parcel.ValueOf(PullRequestField)?.Reference != recorded.Reference)
        {
            await context.SetFieldAsync(PullRequestField, recorded, cancellationToken);
        }

        return Results.With(OpenedVerdict, Name, ("number", pullRequest.Number), ("url", pullRequest.Url), ("head", head));
    }

    /// <summary>The pull request's body: the task's objective, then its criteria as a list.</summary>
    public static string Body(CoreParcel parcel)
    {
        var body = new StringBuilder();
        if (parcel.ValueOf("objective")?.Text is { Length: > 0 } objective)
        {
            body.Append(objective.Trim()).Append("\n\n");
        }

        if (parcel.ValueOf("criteria")?.Items is { Count: > 0 } criteria)
        {
            body.Append("## Criteria\n\n");
            foreach (string criterion in criteria)
            {
                body.Append("- ").Append(criterion.Trim()).Append('\n');
            }
        }

        return body.ToString().TrimEnd('\n') + "\n";
    }
}
