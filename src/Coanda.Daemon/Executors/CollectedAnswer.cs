using Coanda.Core.Flights;

namespace Coanda.Daemon.Executors;

/// <summary>
/// What collecting a session yields: the answer document, or null when the agent produced none,
/// the path of the session's transcript, and the usage the flight's answer event records.
/// </summary>
public sealed record CollectedAnswer(string? Answer, string TranscriptPath, Measurement Usage);
