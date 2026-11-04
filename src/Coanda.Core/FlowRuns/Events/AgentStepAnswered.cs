using System.Text.Json;
using Coanda.Core.Agents;

namespace Coanda.Core.FlowRuns.Events;

/// <summary>An agent answered the flow run's current agent step, with what the answer cost.</summary>
public sealed record AgentStepAnswered(
    Guid FlowRunId,
    string Step,
    string Agent,
    string AgentHash,
    ModelSpec Model,
    JsonElement Answer,
    Measurement Measurement,
    DateTimeOffset AnsweredAt);
