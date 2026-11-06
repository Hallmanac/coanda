using System.Runtime.CompilerServices;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The tests that hold the finalizer thread, which run with no other test beside them: while the thread is
/// held, every other test's store disposal waits for it, and whatever any other test leaves to the
/// finalizers stays unfinalized.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FinalizerHoldCollection
{
    public const string Name = "Tests holding the finalizer thread";
}

/// <summary>
/// Holds the process's finalizer thread from its start until it is released, so whatever the collector
/// queues for finalization meanwhile waits: a SQLite statement whose command was collected stays
/// unfinalized for as long as a test needs it to. It is released after a delay rather than by the test,
/// so a disposal that waits for the finalizers is held for that delay and never for good. A test that holds
/// it belongs to <see cref="FinalizerHoldCollection"/>.
/// </summary>
public sealed class FinalizerHold : IDisposable
{
    // Never disposed: the held finalizer may still be waiting on it when the test is done with the hold.
    private readonly ManualResetEventSlim released = new();
    private Timer? releasing;

    private FinalizerHold()
    {
    }

    /// <summary>Holds the finalizer thread and returns once it is held.</summary>
    public static FinalizerHold Begin()
    {
        var hold = new FinalizerHold();
        using var entered = new ManualResetEventSlim();
        Queue(entered, hold.released);
        GC.Collect();
        if (!entered.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("The finalizer thread did not reach the hold.");
        }

        return hold;
    }

    /// <summary>Releases the finalizer thread once <paramref name="delay"/> has passed.</summary>
    public void ReleaseAfter(TimeSpan delay) => releasing = new Timer(_ => released.Set(), null, delay, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        released.Set();
        releasing?.Dispose();
    }

    /// <summary>Makes the holder unreachable before the collection that queues it, which a caller's frame could otherwise keep alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Queue(ManualResetEventSlim entered, ManualResetEventSlim released) => _ = new Holder(entered, released);

    private sealed class Holder(ManualResetEventSlim entered, ManualResetEventSlim released)
    {
        // Bounded, so a test that never releases it cannot stop the process's finalizers for good.
        ~Holder()
        {
            entered.Set();
            released.Wait(TimeSpan.FromMinutes(1));
        }
    }
}
