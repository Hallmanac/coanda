using System.Text.Json;

namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// The system step of the flow run's current step finished and returned a result document. When the
/// result is the outcome of a dispatch, <paramref name="DispatchId"/> names it; a result recorded
/// before dispatches carried an id, or recorded with no dispatch, replays with none.
/// </summary>
public sealed record SystemStepCompleted(
    Guid FlowRunId,
    string Step,
    string SystemStep,
    JsonElement Result,
    DateTimeOffset CompletedAt,
    Guid? DispatchId = null);
