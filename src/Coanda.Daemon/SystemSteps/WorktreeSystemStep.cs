using Coanda.Core.Projects;
using Coanda.Core.Software;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Makes the flow run's work path, where its agent steps run, a git worktree of the node's clone of the
/// project's repository on the parcel's branch, and answers done with the <c>branch</c> and the
/// <c>head</c> checked out. The commit last recorded as pushed among all the parcel's flow runs (read
/// through <paramref name="flowRuns"/>, a service of the system steps' own) is what tells a person's local
/// rewrite of the branch from a remote someone else moved, and a branch still checked out in the
/// worktree of a flow run that has ended or been stopped is taken over once that flow run's worker, which
/// <paramref name="workers"/> waits on, has returned (<see cref="ProjectClones.CheckOutAsync"/>).
/// A project with no record, or a refusal (a branch another parcel owns, a branch a live flow run's or
/// an unknown worktree holds, a commit tracking <c>.coanda</c> in any letter case or files under it,
/// something at the clone's path that is no repository), pauses with the perform-again lever; a
/// failing git command throws, and the runner pauses quoting it.
/// </summary>
public sealed class WorktreeSystemStep(ProjectService projects, ProjectClones clones, FlowRunService flowRuns, FlowRunWorkers workers) : IFlowSystemStep
{
    public string Name => SoftwareSystemSteps.Worktree;

    public async ValueTask<SystemStepOutcome> PerformAsync(SystemStepRequest request, CancellationToken cancellationToken)
    {
        Project? project = await projects.ShowAsync(request.Principal, request.Project, cancellationToken);
        if (project is null)
        {
            return new SystemStepOutcome.Paused(
                $"Step '{request.Step.Name}' has no repository to make a worktree of: {ProjectService.Unregistered(request.Project)}",
                FlowRunLevers.PerformAgain(request.Step.Name));
        }

        string? lastPushed = await RecordedHeads.OfParcelAsync(flowRuns, request, cancellationToken);
        var holders = new WorktreeHolders(
            async (flowRunId, token) => (await flowRuns.ShowAsync(request.Principal, request.Project, flowRunId, token))?.Status,
            workers.ReturnedAsync);
        Checkout checkout = await clones.CheckOutAsync(project, request.ParcelId, request.FlowRunId, lastPushed, holders, cancellationToken);
        return checkout switch
        {
            Checkout.Ready ready => SystemStepOutcome.DoneWith(Name, ("branch", ready.Branch), ("head", ready.Head)),
            Checkout.Refused refused => new SystemStepOutcome.Paused(
                $"Step '{request.Step.Name}' made no worktree: {refused.Reason}",
                FlowRunLevers.PerformAgain(request.Step.Name)),
            _ => throw new InvalidOperationException($"A checkout ended in a way the worktree system step does not know: {checkout.GetType().Name}."),
        };
    }
}
