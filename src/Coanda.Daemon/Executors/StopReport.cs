using Coanda.Core.FlowRuns;

namespace Coanda.Daemon.Executors;

/// <summary>
/// What stopping a session reports for its attempt in progress: the usage the session reported for
/// it, and whether that usage holds the attempt's cost. A session can report tokens and turns
/// without a cost, as a vendor does when its process is ended before it prints its result; the
/// usage then carries a cost of zero, and <see cref="CostReported"/> says that zero is not a figure.
/// </summary>
public sealed record StopReport(Measurement Usage, bool CostReported = true);
