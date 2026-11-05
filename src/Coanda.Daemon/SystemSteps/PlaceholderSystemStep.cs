namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Stands in for a system step whose real effect is a later task's work (derive-parcels): it answers
/// done with a result naming the system step and marking it as a placeholder, and touches no repository,
/// file, process, or network.
/// </summary>
public sealed class PlaceholderSystemStep(string name) : IFlowSystemStep
{
    public string Name { get; } = name;

    public ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SystemStepAnswer>(SystemStepAnswer.DoneWith(Name, ("placeholder", true)));
}
