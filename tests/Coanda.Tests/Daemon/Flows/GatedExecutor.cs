using Coanda.Core.Agents;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.Flows;

/// <summary>
/// A scripted executor whose collects wait until the test opens the gate, so a judgment can be
/// held in progress; one never opened is an executor that never answers. It records whether the
/// token each stop received was already cancelled, and can fail its first spawns or give up its
/// collects on its own.
/// </summary>
public sealed class GatedExecutor(ScriptedExecutor inner) : IAgentExecutor
{
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource collecting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock records = new();
    private readonly List<bool> stopTokensCancelled = [];
    private int spawnAttempts;

    public ScriptedExecutor Inner => inner;

    /// <summary>When set, every stop is recorded and then fails, as an executor that cannot end its session would.</summary>
    public bool FailStops { get; init; }

    /// <summary>How many of the first spawns fail before returning a handle, as an executor that cannot launch the agent would.</summary>
    public int FailingSpawns { get; init; }

    /// <summary>When set, every collect throws <see cref="OperationCanceledException"/> of its own accord, with no token cancelled.</summary>
    public bool CollectsGiveUp { get; init; }

    /// <summary>Completes once the first session has been spawned.</summary>
    public Task Spawned => spawned.Task;

    /// <summary>Completes once a collect has begun, so the runner is past the spawn and its hand-over.</summary>
    public Task Collecting => collecting.Task;

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
        AgentDefinition agent, string context, string workingDirectory, SpawnCaps caps, string answerSchema, string? credentialProfile, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref spawnAttempts) <= FailingSpawns)
        {
            throw new InvalidOperationException($"Agent '{agent.Name}' could not be launched.");
        }

        SessionHandle handle = await inner.SpawnAsync(agent, context, workingDirectory, caps, answerSchema, credentialProfile, cancellationToken);
        spawned.TrySetResult();
        return handle;
    }

    public async ValueTask<CollectedAnswer> CollectAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        collecting.TrySetResult();
        if (CollectsGiveUp)
        {
            throw new OperationCanceledException($"The executor gave up collecting session {handle.Id}.");
        }

        await gate.Task.WaitAsync(cancellationToken);
        return await inner.CollectAsync(handle, cancellationToken);
    }

    public ValueTask ResumeAsync(SessionHandle handle, string message, CancellationToken cancellationToken) =>
        inner.ResumeAsync(handle, message, cancellationToken);

    public ValueTask SendAsync(SessionHandle handle, string message, CancellationToken cancellationToken) =>
        inner.SendAsync(handle, message, cancellationToken);

    public ValueTask StopAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
        lock (records)
        {
            stopTokensCancelled.Add(cancellationToken.IsCancellationRequested);
        }

        if (FailStops)
        {
            throw new InvalidOperationException($"Session {handle.Id} would not stop.");
        }

        return inner.StopAsync(handle, cancellationToken);
    }

    public ValueTask<IReadOnlyList<SessionHandle>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

    public ValueTask<SessionHandle?> ReattachAsync(SessionHandle handle, CancellationToken cancellationToken) =>
        inner.ReattachAsync(handle, cancellationToken);
}
