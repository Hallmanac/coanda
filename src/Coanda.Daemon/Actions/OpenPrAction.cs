using System.Globalization;
using System.Text;
using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Projects;
using Coanda.Core.Software;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.GitHub;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Pushes the flow run's branch to the project's remote and opens a pull request from it against the
/// primary branch, or reuses the open one whose head it already is, then records that pull request
/// in the task's <c>pull-request</c> field and answers <see cref="OpenedVerdict"/> with its
/// <c>number</c> and <c>url</c> and the <c>head</c> pushed. The remote is parsed and the token read
/// before anything is pushed, so a flow run that cannot open a pull request pushes nothing. A worktree
/// with changes, a remote that is no github.com repository, no token, a failed push, and a request
/// GitHub refuses each pause with the perform-again lever; a push the lease refuses pauses with a
/// lever to stop the flow run, because performing the step again would be refused the same way.
/// A flow run keeps the flow version it started under, and the build flow before this one routed
/// open-pr only on <c>done</c>, so a step that routes on <c>done</c> and not on
/// <see cref="OpenedVerdict"/> is answered <c>done</c> instead, and such a flow run moves past it.
/// </summary>
public sealed class OpenPrAction(
    ParcelService parcels,
    ProjectService projects,
    ProjectClones clones,
    GitHubToken tokens,
    Func<GitHubClient> github) : IFlowAction
{
    /// <summary>The verdict the action answers once the pull request is open.</summary>
    public const string OpenedVerdict = "opened";

    /// <summary>The task field the pull request is recorded in.</summary>
    public const string PullRequestField = "pull-request";

    public string Name => SoftwareActions.OpenPr;

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        string step = request.Step.Name;
        Project? project = await projects.ShowAsync(request.Principal, request.Project, cancellationToken);
        if (project is null)
        {
            return PerformAgain(step, $"has no repository to open a pull request in: {ProjectService.Unregistered(request.Project)}");
        }

        Parcel parcel = await parcels.ShowAsync(request.Principal, request.Project, request.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(request.ParcelId)} in project '{request.Project}'.");

        if (GitHubRepository.Parse(project.Remote) is not { } repository)
        {
            return PerformAgain(
                step,
                $"pushed nothing: the remote '{GitHubRepository.Redact(project.Remote)}' of project '{project.Name}' is not a github.com repository in a form the step reads ({GitHubRepository.Forms}).");
        }

        string token;
        try
        {
            token = await tokens.ReadAsync(cancellationToken);
        }
        catch (GitHubTokenUnavailableException missing)
        {
            return PerformAgain(step, $"pushed nothing: no GitHub token is available, because {missing.Message}");
        }

        Push push = await clones.PushAsync(project, request.ParcelId, request.FlowRunId, cancellationToken);
        string head;
        switch (push)
        {
            case Push.Pushed pushed:
                head = pushed.Head;
                break;
            case Push.Unclean unclean:
                return PerformAgain(
                    step,
                    $"pushed nothing: the worktree has changes that git status reports, which a push would leave out: {string.Join(", ", unclean.Changes)}.");
            case Push.Stale stale:
                return new ActionOutcome.Paused(
                    $"Step '{step}' pushed nothing: someone else updated branch {ProjectClones.BranchOf(request.ParcelId)} on the remote since the node last pushed or fetched it, so git refused the push its lease guards. {stale.Reason}",
                    FlowRunLevers.RemoteBranchMoved(step));
            case Push.Failed failed:
                return PerformAgain(step, $"could not push: {failed.Reason}");
            default:
                throw new InvalidOperationException($"A push ended in a way the open-pr action does not know: {push.GetType().Name}.");
        }

        string branch = ProjectClones.BranchOf(request.ParcelId);
        GitHubClient client = github();
        GitHubPullRequest pullRequest;
        try
        {
            pullRequest = await client.FindOpenAsync(repository, branch, token, cancellationToken)
                ?? await client.OpenAsync(repository, branch, project.PrimaryBranch, parcel.Title, Body(parcel), token, cancellationToken);
        }
        catch (GitHubRefusedException refused)
        {
            return PerformAgain(step, $"pushed {head} but has no pull request: {refused.Message}");
        }
        catch (HttpRequestException unreached)
        {
            return PerformAgain(step, $"pushed {head} but could not reach GitHub: {unreached.Message}");
        }

        string number = pullRequest.Number.ToString(CultureInfo.InvariantCulture);
        FieldValue recorded = FieldValue.OfReference(number, pullRequest.Url);
        if (parcel.ValueOf(PullRequestField)?.Reference != recorded.Reference)
        {
            await parcels.SetFieldAsync(request.Principal, request.Project, request.ParcelId, PullRequestField, recorded, cancellationToken);
        }

        return ActionOutcome.ResultWith(VerdictFor(request.Step), Name, ("number", pullRequest.Number), ("url", pullRequest.Url), ("head", head));
    }

    /// <summary>
    /// The verdict <paramref name="step"/> routes on once the pull request is open: <see cref="OpenedVerdict"/>,
    /// unless the step routes on <c>done</c> and not on it, as the build flow before this one did.
    /// </summary>
    public static string VerdictFor(FlowStep step) =>
        RoutesOn(step, ActionOutcome.DoneVerdict) && !RoutesOn(step, OpenedVerdict)
            ? ActionOutcome.DoneVerdict
            : OpenedVerdict;

    private static bool RoutesOn(FlowStep step, string verdict) =>
        step.Transitions.Any(t =>
            t.Field == ActionOutcome.Verdict
            && t.Value.ValueKind == JsonValueKind.String
            && t.Value.GetString() == verdict);

    /// <summary>The pull request's body: the task's objective, then its criteria as a list.</summary>
    public static string Body(Parcel parcel)
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

    private static ActionOutcome.Paused PerformAgain(string step, string what) =>
        new($"Step '{step}' {what}", FlowRunLevers.PerformAgain(step));
}
