using Coanda.Core.Projects;
using Coanda.Core.Software;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Rebases the task's branch, in the flow run's own worktree, from the recorded head (the <c>head</c>
/// of the latest open-pr or rebase result in the flow run's chain) onto the project's primary branch,
/// pushes the result with a lease that expects the recorded head on the remote, and answers done
/// with the <c>head</c> it pushed (<see cref="ProjectClones.RebaseAsync"/>). A conflict pauses naming
/// the unmerged paths, with a lever that sends the resolution through build; a worktree with
/// changes, a branch tip that is not the recorded head, and a push the lease refuses pause with a
/// lever to stop the flow run, because performing the step again would pause the same way. Any other
/// failing git command pauses with the perform-again lever, quoting the end of its error output.
/// </summary>
public sealed class RebaseAction(ProjectService projects, ProjectClones clones, FlowRunService flowRuns) : IFlowAction
{
    public string Name => SoftwareActions.Rebase;

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        string step = request.Step.Name;
        Project? project = await projects.ShowAsync(request.Principal, request.Project, cancellationToken);
        if (project is null)
        {
            return PerformAgain(step, $"has no repository to rebase in: {ProjectService.Unregistered(request.Project)}");
        }

        if (await RecordedHeads.OfChainAsync(flowRuns, request, cancellationToken) is not { } head)
        {
            return PerformAgain(step, "rebased nothing: no open-pr or rebase result with a head is recorded in the flow run's chain, so there is no pushed commit to rebase from.");
        }

        string branch = ProjectClones.BranchOf(request.ParcelId);
        Rebase rebase;
        try
        {
            rebase = await clones.RebaseAsync(project, request.ParcelId, request.FlowRunId, head, cancellationToken);
        }
        catch (GitCommandException failed)
        {
            return PerformAgain(step, $"could not rebase {branch}: {failed.Message}");
        }

        return rebase switch
        {
            Rebase.Pushed pushed => ActionOutcome.DoneWith(Name, ("branch", branch), ("head", pushed.Head)),
            Rebase.Unclean unclean => Refused(
                step, $"rebased nothing: the worktree of {branch} has changes that git status reports, which a rebase would carry or lose: {string.Join(", ", unclean.Changes)}."),
            Rebase.Moved moved => Refused(
                step,
                $"rebased nothing: branch {branch} is at {Named(moved.Tip)}, not {head}, the commit the flow run's chain recorded as pushed, and the remote's branch, at {Named(moved.Remote)}, is no commit this step noted and pushed, so a rebase would carry commits nobody recorded."),
            Rebase.Conflict conflict => new ActionOutcome.Paused(
                $"Step '{step}' rebased nothing: rebasing {branch} from {head} onto '{project.PrimaryBranch}' stopped on a conflict{(conflict.Commit.Length > 0 ? $" at {conflict.Commit}" : string.Empty)}, where git reports these paths unmerged: {string.Join(", ", conflict.Paths)}. The rebase was aborted and the branch checked out again at {head}; nothing was pushed.",
                FlowRunLevers.RebaseConflict(clones.ClonePathOf(project), branch, project.PrimaryBranch)),
            Rebase.Stale stale => Refused(
                step, $"pushed nothing: someone else updated branch {branch} on the remote, which no longer holds {head}, so git refused the push its lease guards. {stale.Reason}"),
            Rebase.Failed failed => PerformAgain(step, $"could not push the rebased {branch}: {failed.Reason}"),
            _ => throw new InvalidOperationException($"A rebase ended in a way the rebase action does not know: {rebase.GetType().Name}."),
        };
    }

    private static string Named(string commit) =>
        commit.Length > 0
            ? commit
            : "no commit";

    private static ActionOutcome.Paused Refused(string step, string what) =>
        new($"Step '{step}' {what}", FlowRunLevers.RebaseRefused(step));

    private static ActionOutcome.Paused PerformAgain(string step, string what) =>
        new($"Step '{step}' {what}", FlowRunLevers.PerformAgain(step));
}
