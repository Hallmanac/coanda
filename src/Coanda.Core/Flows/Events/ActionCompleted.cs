using System.Text.Json;

namespace Coanda.Core.Flows.Events;

/// <summary>The action of the flow's current step finished and returned a result document.</summary>
public sealed record ActionCompleted(
    Guid FlowId,
    string Step,
    string Action,
    JsonElement Result,
    DateTimeOffset CompletedAt);
