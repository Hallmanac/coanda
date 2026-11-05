using Coanda.Core.Flows;

namespace Coanda.Daemon.SystemSteps;

/// <summary>Answers done with the flow's declared outcome in its result.</summary>
public sealed class EmitOutcomeSystemStep : IFlowSystemStep
{
    public string Name => CoreSystemSteps.EmitOutcome;

    public ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SystemStepAnswer>(SystemStepAnswer.DoneWith(Name, ("outcome", request.Flow.Outcome)));
}
