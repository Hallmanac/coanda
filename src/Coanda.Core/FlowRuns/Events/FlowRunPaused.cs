namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// A flow run stopped to wait for a person; <paramref name="Lever"/> is what that person acts on to
/// resume it. An agent step that paused without an answer records the <paramref name="Usage"/> its
/// attempts reported, since what they cost counts against the budget as an answer's cost does. A
/// pause a system step answered is the outcome of its dispatch, which <paramref name="DispatchId"/>
/// names; any other pause, a person's among them, carries none and leaves a dispatch it finds held.
/// </summary>
public sealed record FlowRunPaused(
    Guid FlowRunId,
    string Reason,
    string Lever,
    string PausedBy,
    DateTimeOffset PausedAt,
    Measurement? Usage = null,
    Guid? DispatchId = null);
