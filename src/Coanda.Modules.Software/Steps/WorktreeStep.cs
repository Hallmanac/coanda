using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// Makes the flow run's work path, where its agent steps run, a git worktree of the node's clone of the
/// project's repository on the parcel's branch, and answers done with the <c>branch</c> and the
/// <c>head</c> checked out. The commit last recorded as pushed among all the parcel's flow runs is what
/// tells a person's local rewrite of the branch from a remote someone else moved, and a branch still
/// checked out in the worktree of a flow run that has ended or been stopped is taken over once nothing of
/// the core's runs for that flow run any more (<see cref="ProjectClones.CheckOutAsync"/>). The step learns
/// that from whichever comes first: the flow run's ended or stopped feed delivery reaching the module
/// (<paramref name="ends"/>), or the core's API showing the flow run as no longer working, which the step
/// reads at once and then every <see cref="WorkingPollInterval"/>. The second is what lets the step go on
/// when the feed never delivers that end: one it skipped because the module was unconfigured for the
/// project, or one whose delivery failed. A project the core shows no record of, or a refusal (a
/// branch another parcel owns, a branch a live flow run's or an unknown worktree holds, a commit
/// tracking <c>.coanda</c> in any letter case or files under it, something at the clone's path that is
/// no repository), pauses with the perform-again lever; a failing git command throws, and the core
/// pauses quoting it.
/// </summary>
public sealed class WorktreeStep(ProjectClones clones, FlowRunEnds ends) : ISoftwareStep
{
    /// <summary>How often a step waiting to take an ended or stopped flow run's worktree over asks the core whether that flow run is still working.</summary>
    public static readonly TimeSpan WorkingPollInterval = TimeSpan.FromMilliseconds(250);

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
            (flowRunId, token) => SettledAsync(context, flowRunId, token));
        Checkout checkout = await clones.CheckOutAsync(project, request.ParcelId, request.FlowRunId, lastPushed, holders, cancellationToken);
        return checkout switch
        {
            Checkout.Ready ready => Results.DoneWith(Name, ("branch", ready.Branch), ("head", ready.Head)),
            Checkout.Refused refused => await context.PerformAgainAsync($"made no worktree: {refused.Reason}", cancellationToken),
            _ => throw new InvalidOperationException($"A checkout ended in a way the worktree step does not know: {checkout.GetType().Name}."),
        };
    }

    /// <summary>
    /// Completes once the ended or stopped flow run <paramref name="flowRunId"/> has nothing of the core's
    /// running for it: when its end reaches the module through the feed, or when the core shows it not
    /// working, whichever is first. A failing read of the core throws, and the core pauses quoting it.
    /// </summary>
    private async Task SettledAsync(StepContext context, Guid flowRunId, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task reached = ends.ReachedAsync(flowRunId, waiting.Token);
        try
        {
            while (!reached.IsCompleted)
            {
                CoreFlowRun holder = await context.Core.FlowRunAsync(context.Request.Project, flowRunId, cancellationToken);
                if (!holder.Working)
                {
                    return;
                }

                await Task.WhenAny(reached, Task.Delay(WorkingPollInterval, waiting.Token));
            }

            await reached;
        }
        finally
        {
            // Releases the wait on the feed when the core answered first, so the module holds nothing for it.
            await waiting.CancelAsync();
        }
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
