using Coanda.Core.Flows;
using Coanda.Core.Software;
using Coanda.Daemon.Flights;
using Coanda.Daemon.Parcels;

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

    /// <summary>The actions the daemon ships: every name in the core catalog and the software catalog.</summary>
    public static FlowActions Shipped(ParcelService parcels, TimeProvider clock) => new(
    [
        new MoveParcelAction(parcels),
        new TimerAction(clock),
        new WaitForHumanAction(),
        new WaitForExternalEventAction(),
        new EmitOutcomeAction(),
        new PlaceholderAction(CoreActions.Spawn),
        new PlaceholderAction(CoreActions.DeriveParcels),
        .. SoftwareActions.All.Select(name => new PlaceholderAction(name)),
    ]);

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        string name = request.Step.Action ?? string.Empty;
        if (!actions.TryGetValue(name, out IFlowAction? action))
        {
            return new ActionOutcome.Paused(
                $"Step '{request.Step.Name}' names the action '{name}', which this daemon has no implementation of.",
                FlightLevers.PerformAgain(request.Step.Name));
        }

        try
        {
            return await action.PerformAsync(request, cancellationToken);
        }
        catch (ActionArgumentException refused)
        {
            return new ActionOutcome.Paused(refused.Message, FlightLevers.PerformAgain(refused.Step));
        }
    }
}
