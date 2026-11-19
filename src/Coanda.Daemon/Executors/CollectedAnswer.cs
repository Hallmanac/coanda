using Coanda.Core.FlowRuns;

namespace Coanda.Daemon.Executors;

/// <summary>
/// What collecting a session yields: the answer document, or null when the agent produced none, the
/// path the session's transcript is kept at once the session has ended, the usage the flow run's
/// answer event records, and the hashes of the session's prompt and transcript as they stood at the
/// collect, or null when the executor keeps no such files.
/// </summary>
public sealed record CollectedAnswer(string? Answer, string TranscriptPath, Measurement Usage, SessionFileHashes? Hashes = null);
