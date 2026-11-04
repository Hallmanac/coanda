namespace Coanda.Daemon.Actions;

/// <summary>
/// Stands in for an action whose real effect is a later task's work (derive-parcels and
/// rebase): it answers done with a result naming the action and marking it as a
/// placeholder, and touches no repository, file, process, or network.
/// </summary>
public sealed class PlaceholderAction(string name) : IFlowAction
{
    public string Name { get; } = name;

    public ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ActionOutcome>(ActionOutcome.DoneWith(Name, ("placeholder", true)));
}
