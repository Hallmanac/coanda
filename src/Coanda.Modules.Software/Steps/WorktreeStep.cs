using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// Makes the flow run's work path, where its agent steps run, a git worktree of the node's clone of the
/// project's repository on the parcel's branch, and answers done with the <c>branch</c> and the
/// <c>head</c> checked out. The commit last recorded as pushed among all the parcel's flow runs is what
/// tells a person's local rewrite of the branch from a remote someone else moved, and a branch still
/// checked out in the worktree of a flow run that has ended or been stopped is taken over once that flow
/// run's end is in <paramref name="ends"/>: once its ended or stopped feed delivery has reached the
/// module, which the step waits for when it has not, or at once when the module's start-up sweep found
/// it ended (<see cref="ProjectClones.CheckOutAsync"/>). A project the core shows no record of, or a refusal (a
/// branch another parcel owns, a branch a live flow run's or an unknown worktree holds, a commit
/// tracking <c>.coanda</c> in any letter case or files under it, something at the clone's path that is
/// no repository), pauses with the perform-again lever; a failing git command throws, and the core
/// pauses quoting it.
/// </summary>
public sealed class WorktreeStep(ProjectClones clones, FlowRunEnds ends) : ISoftwareStep
{
    public string Name => SoftwareSteps.Worktree;

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
            return await context.PerformAgainAsync($"has no repository to make a worktree of: {missing.Message}", cancellationToken);
        }

        string? lastPushed = await RecordedHeads.OfParcelAsync(context, cancellationToken);
        var holders = new WorktreeHolders(
            async (flowRunId, token) => await StatusOfAsync(context, flowRunId, token),
            ends.ReachedAsync);
        Checkout checkout = await clones.CheckOutAsync(project, request.ParcelId, request.FlowRunId, lastPushed, holders, cancellationToken);
        return checkout switch
        {
            Checkout.Ready ready => Results.DoneWith(Name, ("branch", ready.Branch), ("head", ready.Head)),
            Checkout.Refused refused => await context.PerformAgainAsync($"made no worktree: {refused.Reason}", cancellationToken),
            _ => throw new InvalidOperationException($"A checkout ended in a way the worktree step does not know: {checkout.GetType().Name}."),
        };
    }

    /// <summary>The status of the flow run <paramref name="flowRunId"/> as the core names it, or null when the core holds none by that id.</summary>
    private static async Task<string?> StatusOfAsync(StepContext context, Guid flowRunId, CancellationToken cancellationToken)
    {
        try
        {
            return (await context.Core.FlowRunAsync(context.Request.Project, flowRunId, cancellationToken)).Status;
        }
        catch (CoreApiException missing) when (missing.Status == 404)
        {
            return null;
        }
    }
}
