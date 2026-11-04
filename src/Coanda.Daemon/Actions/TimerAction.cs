using Coanda.Core.Contours;

namespace Coanda.Daemon.Actions;

/// <summary>
/// Waits the number of seconds its <c>seconds</c> argument gives, on the daemon's clock, and
/// answers done. The wait is not durable: a daemon stop cancels it and a restart waits again.
/// </summary>
public sealed class TimerAction(TimeProvider clock) : IFlowAction
{
    public const string SecondsArgument = "seconds";

    public string Name => CoreActions.Timer;

    public async ValueTask<ActionOutcome> PerformAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        double seconds = ActionArguments.RequireNonNegativeNumber(request.Step, SecondsArgument);
        await Task.Delay(TimeSpan.FromSeconds(seconds), clock, cancellationToken);
        return ActionOutcome.DoneWith(Name, (SecondsArgument, seconds));
    }
}
