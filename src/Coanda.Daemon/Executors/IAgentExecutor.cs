using Coanda.Core.Agents;

namespace Coanda.Daemon.Executors;

/// <summary>
/// The one seam through which the daemon reaches an agent, whatever vendor runs it. An adapter
/// renders the agent definition into its vendor's invocation; the per-parcel context arrives as a
/// separate value and is never folded into the definition.
/// </summary>
public interface IAgentExecutor
{
    /// <summary>
    /// Starts a session for the agent and returns its handle. When <paramref name="access"/> is
    /// given, every process of the session, a resume's included, runs with its variables set.
    /// </summary>
    ValueTask<SessionHandle> SpawnAsync(
        AgentDefinition agent,
        string context,
        string workingDirectory,
        SpawnCaps caps,
        string answerSchema,
        CredentialProfile? credentialProfile,
        SessionAccess? access,
        CancellationToken cancellationToken);

    /// <summary>Reads the session's latest answer, its transcript path, and its usage.</summary>
    ValueTask<CollectedAnswer> CollectAsync(SessionHandle handle, CancellationToken cancellationToken);

    /// <summary>Continues the session with a message, leaving a new answer to collect.</summary>
    ValueTask ResumeAsync(SessionHandle handle, string message, CancellationToken cancellationToken);

    /// <summary>Delivers a message into a live session without expecting an answer.</summary>
    ValueTask SendAsync(SessionHandle handle, string message, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the session, and reports the usage of its attempt in progress, the one no collect has
    /// returned since the last spawn or resume, or null when the executor reports none. A stop after
    /// a collect returned has no attempt in progress; a collect that failed returned nothing, so its
    /// attempt is still in progress. What the report says is the only way the usage of a judgment
    /// that ended before it answered reaches its flow, and it says when that usage holds no cost.
    /// </summary>
    ValueTask<StopReport?> StopAsync(SessionHandle handle, CancellationToken cancellationToken);

    /// <summary>The handles of the sessions this executor holds that have not been stopped.</summary>
    ValueTask<IReadOnlyList<SessionHandle>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Takes a handle issued before a daemon restart and returns it when its session is still held
    /// and not stopped, or null when the session is gone.
    /// </summary>
    ValueTask<SessionHandle?> ReattachAsync(SessionHandle handle, CancellationToken cancellationToken);
}
