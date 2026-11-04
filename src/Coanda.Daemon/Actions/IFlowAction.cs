namespace Coanda.Daemon.Actions;

/// <summary>
/// Something only the daemon does for an action step: one implementation per action name. It
/// reads the step's arguments and never evaluates them, and answers either a result document the
/// step routes on or a pause.
/// </summary>
public interface IFlowAction
{
    /// <summary>The action name a contour step names, from the core or a domain catalog.</summary>
    string Name { get; }

    ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken);
}
