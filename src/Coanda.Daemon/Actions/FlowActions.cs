using Coanda.Core.Flows;
using Coanda.Daemon.Commands;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.GitHub;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Repositories;

namespace Coanda.Daemon.Actions;

/// <summary>
/// The registered actions, one implementation per name, and the one place an action step is
/// performed: it finds the step's action and turns a missing or ill-kinded argument into a pause.
/// </summary>
public sealed class FlowActions
{
    private readonly Dictionary<string, IFlowAction> actions = new(StringComparer.Ordinal);

    public FlowActions(IEnumerable<IFlowAction> registered)
    {
        foreach (IFlowAction action in registered)
        {
            if (!actions.TryAdd(action.Name, action))
            {
                throw new InvalidOperationException($"The action '{action.Name}' is registered twice; each name has one implementation.");
            }
        }
    }

    /// <summary>The names of the registered actions.</summary>
    public IReadOnlySet<string> Names => actions.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The actions the daemon ships: every name in the core catalog and the software catalog. The
    /// worktree, gates, open-pr, and rebase actions run their commands through <paramref name="runner"/>, the
    /// gates action ending each of its commands after <paramref name="gatesCommandLimit"/>; open-pr and
    /// gated-merge read their token through it too and reach GitHub through the client
    /// <paramref name="github"/> gives them for each performance. Gated-merge and rebase read the head
    /// the flow run's chain recorded as pushed, and the worktree action the head last pushed for the
    /// parcel and the status of a flow run whose worktree holds its branch, through <paramref name="flowRuns"/>,
    /// a service of their own, since the daemon's own flow run service hands resumes to the runner that
    /// performs these actions; for the same reason the worktree action waits on another flow run's worker
    /// through <paramref name="workers"/>, which the runner publishes to. Derive-parcels is still a placeholder.
    /// </summary>
    public static FlowActions Shipped(
        ParcelService parcels,
        ProjectService projects,
        ProjectClones clones,
        ICommandRunner runner,
        NodeHome home,
        TimeProvider clock,
        TimeSpan gatesCommandLimit,
        Func<GitHubClient> github,
        FlowRunService flowRuns,
        FlowRunWorkers workers) => new(
    [
        new MoveParcelAction(parcels),
        new TimerAction(clock),
        new WaitForHumanAction(),
        new WaitForExternalEventAction(),
        new EmitOutcomeAction(),
        new PlaceholderAction(CoreActions.DeriveParcels),
        new WorktreeAction(projects, clones, flowRuns, workers),
        new GatesAction(projects, runner, home, gatesCommandLimit),
        new OpenPrAction(parcels, projects, clones, new GitHubToken(runner, home), github),
        new GatedMergeAction(parcels, projects, flowRuns, new GitHubToken(runner, home), github),
        new RebaseAction(projects, clones, flowRuns),
    ]);

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        string name = request.Step.Action ?? string.Empty;
        if (!actions.TryGetValue(name, out IFlowAction? action))
        {
            return new ActionOutcome.Paused(
                $"Step '{request.Step.Name}' names the action '{name}', which this daemon has no implementation of.",
                FlowRunLevers.PerformAgain(request.Step.Name));
        }

        try
        {
            return await action.PerformAsync(request, cancellationToken);
        }
        catch (ActionArgumentException refused)
        {
            return new ActionOutcome.Paused(refused.Message, FlowRunLevers.PerformAgain(refused.Step));
        }
    }
}
