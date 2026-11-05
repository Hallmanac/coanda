using Coanda.Core.Flows;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Waits the number of seconds its <c>seconds</c> argument gives, on the daemon's clock, and
/// answers done. The wait is not durable: a daemon stop cancels it and a restart waits again.
/// </summary>
public sealed class TimerSystemStep(TimeProvider clock) : IFlowSystemStep
{
    public const string SecondsArgument = "seconds";

    public string Name => CoreSystemSteps.Timer;

    public async ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        double seconds = SystemStepArguments.RequireNonNegativeNumber(request.Step, SecondsArgument);
        await Task.Delay(TimeSpan.FromSeconds(seconds), clock, cancellationToken);
        return SystemStepAnswer.DoneWith(Name, (SecondsArgument, seconds));
    }
}
