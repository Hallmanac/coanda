namespace Coanda.Core.Contours;

/// <summary>
/// What a flow run does next, as the <see cref="Interpreter"/> decides it: go to a named step, pause
/// with a reason, or end carrying the outcome the contour declares. The three cases are the only ones;
/// the private constructor closes the hierarchy.
/// </summary>
public abstract record FlowRunMove
{
    private FlowRunMove()
    {
    }

    /// <summary>Enter the step named <paramref name="Step"/>.</summary>
    public sealed record GoTo(string Step) : FlowRunMove;

    /// <summary>Stop and wait for a human; <paramref name="Reason"/> says why.</summary>
    public sealed record Pause(string Reason) : FlowRunMove;

    /// <summary>The flow run is over and carries <paramref name="Outcome"/>, the one its contour declares.</summary>
    public sealed record End(string Outcome) : FlowRunMove;
}
