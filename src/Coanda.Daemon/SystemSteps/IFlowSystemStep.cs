namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Something only the daemon does for a system step: one implementation per system step name. It
/// reads the step's arguments and never evaluates them, and answers either a result document the
/// step routes on or a pause.
/// </summary>
public interface IFlowSystemStep
{
    /// <summary>The system step name a flow step names, from the core or a domain catalog.</summary>
    string Name { get; }

    ValueTask<SystemStepOutcome> PerformAsync(SystemStepRequest request, CancellationToken cancellationToken);
}
