using Coanda.Core.Agents;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.Flights;

/// <summary>
/// A scripted executor whose collects wait until the test opens the gate, so a judgment can be
/// held in progress; one never opened is an executor that never answers. It records whether the
/// token each stop received was already cancelled.
/// </summary>
public sealed class GatedExecutor(ScriptedExecutor inner) : IAgentExecutor
{
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock records = new();
    private readonly List<bool> stopTokensCancelled = [];

    public ScriptedExecutor Inner => inner;

    /// <summary>When set, every stop is recorded and then fails, as an executor that cannot end its session would.</summary>
    public bool FailStops { get; init; }

    /// <summary>Completes once the first session has been spawned.</summary>
    public Task Spawned => spawned.Task;

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
        SessionHandle handle = await inner.SpawnAsync(agent, context, workingDirectory, caps, answerSchema, credentialProfile, cancellationToken);
        spawned.TrySetResult();
        return handle;
    }

    public async ValueTask<CollectedAnswer> CollectAsync(SessionHandle handle, CancellationToken cancellationToken)
    {
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
