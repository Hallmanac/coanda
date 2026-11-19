using Coanda.Core.FlowRuns;

namespace Coanda.Daemon.Executors;

/// <summary>
/// One canned answer in a <see cref="ScriptedExecutor"/>'s script: what a collect returns. The
/// scripted executor writes no files, so the hashes a collect returns are whatever the entry names.
/// </summary>
public sealed record ScriptEntry(string? Answer, string TranscriptPath, Measurement Usage, SessionFileHashes? Hashes = null);
