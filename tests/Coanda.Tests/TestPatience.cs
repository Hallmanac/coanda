namespace Coanda.Tests;

/// <summary>How long a test waits on daemon or flow-run work before it fails.</summary>
public static class TestPatience
{
    /// <summary>
    /// The bound every test-side wait on daemon and flow-run work takes, so a wait whose condition
    /// never holds still fails, only later.
    /// </summary>
    /// <remarks>
    /// It is far longer than the work takes on an idle machine because xUnit runs test classes in
    /// parallel by default, with one thread per logical CPU. The tests that wait with this bound each
    /// start a daemon host, and some also start child processes, so on a busy or many-core machine the
    /// concurrent tests contend for the processors and the work they wait on can take many times longer
    /// than it does alone. A 15-second bound ran out under that contention on a 28-CPU machine while
    /// the work itself was still finishing. A passing test never waits this long, so the bound only
    /// lengthens a failing run.
    /// </remarks>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(90);
}
