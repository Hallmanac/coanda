using System.Text.Json;

namespace Coanda.Core.FlowRuns.Events;

/// <summary>The action of the flow run's current step finished and returned a result document.</summary>
public sealed record ActionCompleted(
    Guid FlowRunId,
    string Step,
    string Action,
    JsonElement Result,
    DateTimeOffset CompletedAt);
