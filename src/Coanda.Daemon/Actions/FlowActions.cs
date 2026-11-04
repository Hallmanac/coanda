using Coanda.Core.Contours;
using Coanda.Core.Software;
using Coanda.Daemon.Commands;
using Coanda.Daemon.Flows;
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
    /// worktree, gates, and open-pr actions run their commands through <paramref name="runner"/>, the
    /// gates action ending each of its commands after <paramref name="gatesCommandLimit"/>, and open-pr
    /// reaches GitHub through the client <paramref name="github"/> gives it for each performance;
    /// derive-parcels, gated-merge, and rebase are still placeholders.
    /// </summary>
    public static FlowActions Shipped(
        ParcelService parcels,
        ProjectService projects,
        ProjectClones clones,
        ICommandRunner runner,
        NodeHome home,
        TimeProvider clock,
        TimeSpan gatesCommandLimit,
        Func<GitHubClient> github) => new(
    [
        new MoveParcelAction(parcels),
        new TimerAction(clock),
        new WaitForHumanAction(),
        new WaitForExternalEventAction(),
        new EmitOutcomeAction(),
        new PlaceholderAction(CoreActions.DeriveParcels),
        new WorktreeAction(projects, clones),
        new GatesAction(projects, runner, home, gatesCommandLimit),
        new OpenPrAction(parcels, projects, clones, new GitHubToken(runner, home), github),
        .. SoftwareActions.All
            .Where(name => name is not (SoftwareActions.Worktree or SoftwareActions.Gates or SoftwareActions.OpenPr))
            .Select(name => new PlaceholderAction(name)),
    ]);

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        string name = request.Step.Action ?? string.Empty;
        if (!actions.TryGetValue(name, out IFlowAction? action))
        {
            return new ActionOutcome.Paused(
                $"Step '{request.Step.Name}' names the action '{name}', which this daemon has no implementation of.",
                FlowLevers.PerformAgain(request.Step.Name));
        }

        try
        {
            return await action.PerformAsync(request, cancellationToken);
        }
        catch (ActionArgumentException refused)
        {
            return new ActionOutcome.Paused(refused.Message, FlowLevers.PerformAgain(refused.Step));
        }
    }
}
