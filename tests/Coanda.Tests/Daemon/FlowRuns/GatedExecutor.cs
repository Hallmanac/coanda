using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A scripted executor whose collects wait until the test opens the gate, so an agent step can be
/// held in progress; one never opened is an executor that never answers. It records whether the
/// token each stop received was already cancelled, and can fail its first spawns, give up its
/// collects on its own, let its first collects pass the gate, hold a collect past its token's
/// cancellation, report a usage for an attempt in progress when its session is stopped, and call a
/// test back before each spawn, each listing, each reattach, and each stop.
/// </summary>
public sealed class GatedExecutor(ScriptedExecutor inner) : IAgentExecutor
{
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource collecting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock records = new();
    private readonly List<bool> stopTokensCancelled = [];
    private readonly HashSet<Guid> inProgress = [];
    private int spawnAttempts;
    private int collects;

    public ScriptedExecutor Inner => inner;

    /// <summary>While set, every stop is recorded and then fails, as an executor that cannot end its session would.</summary>
    public bool FailStops { get; set; }

    /// <summary>How many of the first spawns fail before returning a handle, as an executor that cannot launch the agent would.</summary>
    public int FailingSpawns { get; init; }

    /// <summary>
    /// While set, every collect throws <see cref="TaskCanceledException"/> of its own accord, with no
    /// token cancelled, as an executor whose own timeout or I/O gives up would.
    /// </summary>
    public bool CollectsGiveUp { get; set; }

    /// <summary>How many of the first collects pass without waiting at the gate.</summary>
    public int FreeCollects { get; init; }

    /// <summary>When set, a collect held at the gate ignores its token's cancellation and answers once the gate opens.</summary>
    public bool HeldCollectsIgnoreCancellation { get; init; }

    /// <summary>
    /// What a stop reports for a session whose attempt is in progress: a collect has begun since its
    /// spawn or resume and none has returned. Null reports nothing, as the scripted executor does.
    /// </summary>
    public Measurement? InProgressUsage { get; init; }

    /// <summary>Whether what a stop reports for an attempt in progress holds its cost; when not, the usage is tokens and turns alone.</summary>
    public bool InProgressCostReported { get; init; } = true;

    /// <summary>Called with each spawn's session access before the session is started, so a test can read what was stored by then.</summary>
    public Func<SessionAccess?, Task>? BeforeSpawn { get; init; }

    /// <summary>Called with each reattach's handle before the reattach is made, so a test can write between a read and the reattach.</summary>
    public Func<SessionHandle, Task>? BeforeReattach { get; init; }

    /// <summary>Called before each listing of the sessions, so a test can write between the start-up sweep's reads and its stops.</summary>
    public Func<Task>? BeforeList { get; init; }

    /// <summary>Called with each stop's handle before the stop is made, so a test can read what was recorded by then.</summary>
    public Func<SessionHandle, Task>? BeforeStop { get; init; }

    /// <summary>Completes once the first session has been spawned.</summary>
    public Task Spawned => spawned.Task;

    /// <summary>Completes once a collect has begun, so the runner is past the spawn and its hand-over.</summary>
    public Task Collecting => collecting.Task;

    /// <summary>Completes once a collect is waiting at the closed gate.</summary>
    public Task Holding => holding.Task;

    /// <summary>For each stop, whether the token it received was already cancelled.</summary>
    public IReadOnlyList<bool> StopTokensCancelled
    {
        get
        {
            lock (records)
            {
                return [.. stopTokensCancelled];
            }
        }
    }

    public void Open() => gate.TrySetResult();

    public async ValueTask<SessionHandle> SpawnAsync(
        AgentDefinition agent, string context, string workingDirectory, SpawnCaps caps, AnswerSchema answerSchema, CredentialProfile? credentialProfile, SessionAccess? access, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref spawnAttempts) <= FailingSpawns)
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' could not be launched.");
        }

        if (BeforeSpawn is { } before)
        {
            await before(access);
        }

        SessionHandle handle = await inner.SpawnAsync(agent, context, workingDirectory, caps, answerSchema, credentialProfile, access, cancellationToken);
        spawned.TrySetResult();
        return handle;
    }

    public async ValueTask<CollectedAnswer> CollectAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        lock (records)
        {
            inProgress.Add(handle.Id);
        }

        collecting.TrySetResult();
        if (CollectsGiveUp)
        {
            throw new TaskCanceledException($"The executor gave up collecting session {handle.Id}.");
        }

        if (Interlocked.Increment(ref collects) > FreeCollects)
        {
            if (!gate.Task.IsCompleted)
            {
                holding.TrySetResult();
            }

            if (HeldCollectsIgnoreCancellation)
            {
                await gate.Task;
                cancellationToken = CancellationToken.None;
            }
            else
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
        }

        CollectedAnswer collected = await inner.CollectAsync(handle, cancellationToken);
        lock (records)
        {
            inProgress.Remove(handle.Id);
        }

        return collected;
    }

    public ValueTask ResumeAsync(SessionHandle handle, string message, CancellationToken cancellationToken) =>
        inner.ResumeAsync(handle, message, cancellationToken);

    public ValueTask SendAsync(SessionHandle handle, string message, CancellationToken cancellationToken) =>
        inner.SendAsync(handle, message, cancellationToken);

    public async ValueTask<StopReport?> StopAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        if (BeforeStop is { } before)
        {
            await before(handle);
        }

        bool attempting;
        lock (records)
        {
            stopTokensCancelled.Add(cancellationToken.IsCancellationRequested);

            // A stop that fails ends nothing, so the attempt it would have ended is still in progress.
            attempting = !FailStops && inProgress.Remove(handle.Id);
        }

        if (FailStops)
        {
            throw new InvalidOperationException($"Session {handle.Id} would not stop.");
        }

        StopReport? reported = await inner.StopAsync(handle, cancellationToken);
        return attempting && InProgressUsage is { } usage
            ? new StopReport(usage, InProgressCostReported)
            : attempting ? null : reported;
    }

    public async ValueTask<IReadOnlyList<SessionHandle>> ListAsync(CancellationToken cancellationToken)
    {
        if (BeforeList is { } before)
        {
            await before();
        }

        return await inner.ListAsync(cancellationToken);
    }

    public async ValueTask<Reattachment?> ReattachAsync(SessionHandle handle, SessionAccess? access, CancellationToken cancellationToken)
    {
        if (BeforeReattach is { } before)
        {
            await before(handle);
        }

        return await inner.ReattachAsync(handle, access, cancellationToken);
    }
}
