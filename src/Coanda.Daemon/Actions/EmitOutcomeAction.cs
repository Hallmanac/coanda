using Coanda.Core.Contours;

namespace Coanda.Daemon.Actions;

/// <summary>Answers done with the contour's declared outcome in its result.</summary>
public sealed class EmitOutcomeAction : IFlowAction
{
    public string Name => CoreActions.EmitOutcome;

    public ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ActionOutcome>(ActionOutcome.DoneWith(Name, ("outcome", request.Contour.Outcome)));
}
