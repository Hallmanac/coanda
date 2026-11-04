namespace Coanda.Core.Flows;

/// <summary>
/// What a flight does next, as the <see cref="Interpreter"/> decides it: go to a named step, pause
/// with a reason, or end carrying the outcome the flow declares. The three cases are the only ones;
/// the private constructor closes the hierarchy.
/// </summary>
public abstract record FlightMove
{
    private FlightMove()
    {
    }

    /// <summary>Enter the step named <paramref name="Step"/>.</summary>
    public sealed record GoTo(string Step) : FlightMove;

    /// <summary>Stop and wait for a human; <paramref name="Reason"/> says why.</summary>
    public sealed record Pause(string Reason) : FlightMove;

    /// <summary>The flight is over and carries <paramref name="Outcome"/>, the one its flow declares.</summary>
    public sealed record End(string Outcome) : FlightMove;
}
