using System.Text.Json;
using Coanda.Core.Agents;

namespace Coanda.Core.Flows.Events;

/// <summary>An agent answered the flow's current judgment step, with what the answer cost.</summary>
public sealed record JudgmentAnswered(
    Guid FlowId,
    string Step,
    string Agent,
    string AgentHash,
    ModelSpec Model,
    JsonElement Answer,
    Measurement Measurement,
    DateTimeOffset AnsweredAt);
