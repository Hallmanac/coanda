using System.Text.Json;

namespace Coanda.Core.FlowRuns.Events;

/// <summary>The system step of the flow run's current step finished and returned a result document.</summary>
public sealed record SystemStepCompleted(
    Guid FlowRunId,
    string Step,
    string SystemStep,
    JsonElement Result,
    DateTimeOffset CompletedAt);
