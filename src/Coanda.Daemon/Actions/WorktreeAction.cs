using Coanda.Core.Projects;
using Coanda.Core.Software;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Makes the flow's work path, where its judgments run, a git worktree of the node's clone of the
/// project's repository on the parcel's branch, and answers done with the <c>branch</c> and the
/// <c>head</c> checked out. A project with no record, or a refusal (a branch another parcel owns, a
/// commit tracking files under <c>.coanda/</c>, something at the clone's path that is no repository),
/// pauses with the perform-again lever; a failing git command throws, and the runner pauses quoting it.
/// </summary>
public sealed class WorktreeAction(ProjectService projects, ProjectClones clones) : IFlowAction
{
    public string Name => SoftwareActions.Worktree;

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        Project? project = await projects.ShowAsync(request.Principal, request.Project, cancellationToken);
        if (project is null)
        {
            return new ActionOutcome.Paused(
                $"Step '{request.Step.Name}' has no repository to make a worktree of: {ProjectService.Unregistered(request.Project)}",
                FlowLevers.PerformAgain(request.Step.Name));
        }

        Checkout checkout = await clones.CheckOutAsync(project, request.ParcelId, request.FlowId, cancellationToken);
        return checkout switch
        {
            Checkout.Ready ready => ActionOutcome.DoneWith(Name, ("branch", ready.Branch), ("head", ready.Head)),
            Checkout.Refused refused => new ActionOutcome.Paused(
                $"Step '{request.Step.Name}' made no worktree: {refused.Reason}",
                FlowLevers.PerformAgain(request.Step.Name)),
            _ => throw new InvalidOperationException($"A checkout ended in a way the worktree action does not know: {checkout.GetType().Name}."),
        };
    }
}
