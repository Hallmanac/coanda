namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Something only the daemon does for a system step: one implementation per system step name. A
/// dispatch reads the step's arguments and never evaluates them, and answers a result document the
/// step routes on, a pause, or running. Dispatching again under the same dispatch id must be safe,
/// and the step, not the daemon, makes it so. A step that always answers done or paused keeps no
/// record of its dispatches, so it answers unknown to a status and has nothing to stop.
/// </summary>
public interface IFlowSystemStep
{
    /// <summary>The system step name a flow step names, from the core or a domain catalog.</summary>
    string Name { get; }

    /// <summary>Does the step under the dispatch id the request carries, which the flow run already records.</summary>
    ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken);

    /// <summary>
    /// Where the dispatch <paramref name="dispatchId"/> stands, asked after a daemon restart instead
    /// of dispatching it again. A step that never answers running knows no dispatch, so it answers unknown.
    /// </summary>
    ValueTask<SystemStepStatus> StatusAsync(Guid dispatchId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SystemStepStatus>(new SystemStepStatus.Unknown());

    /// <summary>
    /// Stops the dispatch <paramref name="dispatchId"/>, which holds no outcome: a person stopped its
    /// flow run, or a resume replaces it with a new dispatch. A step that never answers running has
    /// nothing left running, so it does nothing.
    /// </summary>
    ValueTask StopAsync(Guid dispatchId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
