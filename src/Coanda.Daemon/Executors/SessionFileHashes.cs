using Coanda.Core.FlowRuns;

namespace Coanda.Daemon.Executors;

/// <summary>
/// The <see cref="FileHash"/> of the two files a session leaves on the node that ran it, as they stood
/// when its answer was collected: the prompt its processes were given on standard input, every
/// attempt's in the order sent, and its transcript.
/// </summary>
public sealed record SessionFileHashes(string Prompt, string Transcript);
