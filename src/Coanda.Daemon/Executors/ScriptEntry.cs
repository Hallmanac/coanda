using Coanda.Core.FlowRuns;

namespace Coanda.Daemon.Executors;

/// <summary>One canned answer in a <see cref="ScriptedExecutor"/>'s script: what a collect returns.</summary>
public sealed record ScriptEntry(string? Answer, string TranscriptPath, Measurement Usage);
