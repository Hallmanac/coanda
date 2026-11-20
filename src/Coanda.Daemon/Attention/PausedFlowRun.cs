namespace Coanda.Daemon.Attention;

/// <summary>
/// A paused flow run this node started, as the attention query gives it: where it is, what it runs on,
/// the step it paused on, why it paused, what a person acts on to resume it, and when its latest pause
/// was recorded.
/// </summary>
public sealed record PausedFlowRun(
    string Project,
    Guid FlowRunId,
    Guid ParcelId,
    string ParcelTitle,
    string FlowName,
    string Step,
    string Reason,
    string Lever,
    DateTimeOffset PausedAt);
