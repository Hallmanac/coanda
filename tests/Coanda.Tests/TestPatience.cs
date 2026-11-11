namespace Coanda.Tests;

/// <summary>How long a test waits on daemon or flow-run work before it fails.</summary>
public static class TestPatience
{
    /// <summary>
    /// The bound for the test-side waits on daemon and flow-run work that used to wait a fixed 15
    /// seconds, so a wait whose condition never holds still fails, only later. Waits that had some
    /// other bound, such as 30 seconds, keep it, even though it is now shorter than this one.
    /// </summary>
    /// <remarks>
    /// It is far longer than the work takes on an idle machine because xUnit runs test classes in
    /// parallel, at most four at once (the cap set in xunit.runner.json). The tests that wait with this
    /// bound each start a daemon host, and some also start child processes, so on a busy machine the
    /// concurrent tests contend for the processors and the work they wait on can take many times longer
    /// than it does alone. Tests that pass when run alone have run out their 15-second bound
    /// in full-suite runs. A passing test never waits this long, so the bound only lengthens a
    /// failing run.
    /// </remarks>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(90);
}
