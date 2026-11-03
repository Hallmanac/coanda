namespace Coanda.Daemon.Executors;

/// <summary>
/// Names one agent session an executor holds. It is plain data, so the runner can keep it across a
/// daemon restart and hand it back to <see cref="IAgentExecutor.ReattachAsync"/>.
/// </summary>
public sealed record SessionHandle(Guid Id, string Agent);
